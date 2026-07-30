using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace StormSocket.Benchmark.Soak;

/// <summary>
/// A minimal hand-rolled WebSocket client used for the two things the library's own client cannot
/// do: send a message split across continuation frames, and drop the connection with a TCP reset
/// instead of a closing handshake.
/// </summary>
internal sealed class RawWebSocketClient : IDisposable
{
    private const int MaxHandshakeResponseBytes = 4 * 1024;

    private readonly Socket _socket;

    public RawWebSocketClient()
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
            // Closing with a zero linger sends RST rather than FIN, which is what a crashed or killed
            // peer produces. That is the teardown the server has to unwind without leaving a session,
            // a group entry or a socket behind.
            LingerState = new LingerOption(true, 0),
        };
    }

    /// <summary>Connects and completes the upgrade handshake.</summary>
    public async Task ConnectAsync(int port, CancellationToken cancellationToken)
    {
        await _socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), cancellationToken).ConfigureAwait(false);

        string key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        string request =
            $"GET /soak HTTP/1.1\r\n" +
            $"Host: 127.0.0.1:{port}\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Key: {key}\r\n" +
            "Sec-WebSocket-Version: 13\r\n\r\n";

        await _socket.SendAsync(Encoding.ASCII.GetBytes(request), SocketFlags.None, cancellationToken).ConfigureAwait(false);

        byte[] buffer = new byte[MaxHandshakeResponseBytes];
        int filled = 0;

        while (!EndsWithHeaderTerminator(buffer, filled))
        {
            if (filled == buffer.Length)
            {
                throw new IOException("Upgrade response exceeded the buffer without terminating.");
            }

            int read = await _socket.ReceiveAsync(buffer.AsMemory(filled), SocketFlags.None, cancellationToken).ConfigureAwait(false);
            if (read is 0)
            {
                throw new IOException("Server closed the connection during the upgrade handshake.");
            }

            filled += read;
        }

        string status = Encoding.ASCII.GetString(buffer, 0, Math.Min(filled, 12));
        if (!status.StartsWith("HTTP/1.1 101", StringComparison.Ordinal))
        {
            throw new IOException($"Upgrade rejected: {status}");
        }
    }

    /// <summary>Sends one text message split into masked fragments of <paramref name="fragmentSize"/> bytes.</summary>
    public async Task SendFragmentedTextAsync(ReadOnlyMemory<byte> utf8Payload, int fragmentSize, CancellationToken cancellationToken)
    {
        int offset = 0;

        while (offset < utf8Payload.Length)
        {
            int chunk = Math.Min(fragmentSize, utf8Payload.Length - offset);
            byte opCode = offset is 0 ? (byte)0x01 : (byte)0x00;
            bool final = offset + chunk == utf8Payload.Length;

            byte[] frame = BuildMaskedFrame(opCode, final, utf8Payload.Span.Slice(offset, chunk));
            await _socket.SendAsync(frame, SocketFlags.None, cancellationToken).ConfigureAwait(false);

            offset += chunk;
        }
    }

    /// <summary>
    /// Reads whatever the server sends back until <paramref name="budget"/> is spent, then returns.
    /// </summary>
    /// <remarks>
    /// Stopping mid-echo is the point: the reset that follows arrives while the server still has a
    /// write in flight, which is the harshest path through its teardown.
    /// </remarks>
    public async Task DrainAsync(TimeSpan budget, CancellationToken cancellationToken)
    {
        using CancellationTokenSource budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budgetCts.CancelAfter(budget);

        byte[] buffer = new byte[16 * 1024];

        try
        {
            while (await _socket.ReceiveAsync(buffer, SocketFlags.None, budgetCts.Token).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (OperationCanceledException)
        {
            // Budget spent, or the run is shutting down.
        }
        catch (SocketException)
        {
            // The server got there first; the connection is going away either way.
        }
    }

    private static byte[] BuildMaskedFrame(byte opCode, bool final, ReadOnlySpan<byte> payload)
    {
        int lengthBytes = payload.Length switch
        {
            <= 125 => 0,
            <= ushort.MaxValue => 2,
            _ => 8,
        };

        byte[] frame = new byte[2 + lengthBytes + 4 + payload.Length];
        frame[0] = (byte)((final ? 0x80 : 0x00) | opCode);

        int index;
        if (lengthBytes is 0)
        {
            frame[1] = (byte)(0x80 | payload.Length);
            index = 2;
        }
        else if (lengthBytes is 2)
        {
            frame[1] = 0x80 | 126;
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)payload.Length);
            index = 4;
        }
        else
        {
            frame[1] = 0x80 | 127;
            BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(2), (ulong)payload.Length);
            index = 10;
        }

        Span<byte> mask = frame.AsSpan(index, 4);
        RandomNumberGenerator.Fill(mask);
        index += 4;

        for (int i = 0; i < payload.Length; i++)
        {
            frame[index + i] = (byte)(payload[i] ^ mask[i & 3]);
        }

        return frame;
    }

    private static bool EndsWithHeaderTerminator(byte[] buffer, int length)
    {
        if (length < 4)
        {
            return false;
        }

        for (int i = 0; i <= length - 4; i++)
        {
            if (buffer[i] is 13 && buffer[i + 1] is 10 && buffer[i + 2] is 13 && buffer[i + 3] is 10)
            {
                return true;
            }
        }

        return false;
    }

    public void Dispose() => _socket.Dispose();
}
