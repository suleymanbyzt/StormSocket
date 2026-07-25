using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using StormSocket.Server;
using StormSocket.Session;
using StormSocket.WebSocket;
using Xunit;

namespace StormSocket.Tests;

/// <summary>
/// Protocol-level conformance tests driven over a raw socket, because the defects they cover are
/// only reachable by writing bytes a well-behaved client would never send.
/// </summary>
public class WsProtocolComplianceTests
{
    private static async Task<(StormWebSocketServer server, int port)> StartServerAsync(
        WebSocketOptions? wsOptions = null,
        Action<StormWebSocketServer>? configure = null)
    {
        StormWebSocketServer server = new(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            WebSocket = wsOptions ?? new WebSocketOptions
            {
                Heartbeat = new() { PingInterval = TimeSpan.Zero },
            },
        });

        configure?.Invoke(server);
        await server.StartAsync();
        return (server, ((IPEndPoint)server.LocalEndPoint!).Port);
    }

    private static async Task<NetworkStream> HandshakeAsync(int port, string? extensions = null)
    {
        TcpClient tcp = new();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        NetworkStream stream = tcp.GetStream();

        string key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        string request =
            $"GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            $"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n" +
            (extensions is null ? "" : $"Sec-WebSocket-Extensions: {extensions}\r\n") +
            "\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));

        byte[] buffer = new byte[2048];
        int read = await stream.ReadAsync(buffer);
        string response = Encoding.ASCII.GetString(buffer, 0, read);
        Assert.StartsWith("HTTP/1.1 101", response);
        return stream;
    }

    private static byte[] MaskedFrame(byte opCodeByte, ReadOnlySpan<byte> payload)
    {
        byte[] mask = RandomNumberGenerator.GetBytes(4);
        byte[] frame = new byte[2 + 4 + payload.Length];
        frame[0] = opCodeByte;
        frame[1] = (byte)(0x80 | payload.Length);
        mask.CopyTo(frame.AsSpan(2));

        for (int i = 0; i < payload.Length; i++)
        {
            frame[6 + i] = (byte)(payload[i] ^ mask[i & 3]);
        }

        return frame;
    }

    /// <summary>Reads one frame header and returns (opcode, closeCode) — closeCode is 0 for non-Close frames.</summary>
    private static async Task<(int OpCode, int CloseCode)> ReadFrameAsync(NetworkStream stream)
    {
        byte[] buffer = new byte[256];
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));
        int read = await stream.ReadAsync(buffer, cts.Token);
        Assert.True(read >= 2, "expected a frame, got EOF");

        int opCode = buffer[0] & 0x0F;
        int closeCode = opCode == 0x8 && read >= 4 ? (buffer[2] << 8) | buffer[3] : 0;
        return (opCode, closeCode);
    }

    [Fact]
    public async Task UnmaskedClientFrame_FailsConnectionWithProtocolError()
    {
        (StormWebSocketServer server, int port) = await StartServerAsync();
        await using StormWebSocketServer _ = server;

        List<string> received = [];
        server.OnMessageReceived += (_, msg) =>
        {
            lock (received) received.Add(msg.Text);
            return ValueTask.CompletedTask;
        };

        using NetworkStream stream = await HandshakeAsync(port);

        // RFC 6455 Section 5.1: a client frame with MASK=0 must fail the connection.
        byte[] payload = "unmasked"u8.ToArray();
        byte[] frame = new byte[2 + payload.Length];
        frame[0] = 0x81;
        frame[1] = (byte)payload.Length;
        payload.CopyTo(frame.AsSpan(2));
        await stream.WriteAsync(frame);

        (int opCode, int closeCode) = await ReadFrameAsync(stream);
        Assert.Equal(0x8, opCode);
        Assert.Equal((int)WsCloseStatus.ProtocolError, closeCode);

        lock (received)
        {
            Assert.Empty(received);
        }
    }

    [Theory]
    [InlineData(0x81)] // text
    [InlineData(0x82)] // binary
    public async Task ZeroLengthMessage_IsDeliveredToTheApplication(byte opCodeByte)
    {
        // A zero-length message is legal (RFC 6455 5.2) and used as a keepalive by real clients.
        // It used to be swallowed by the middleware hook, whose "empty means suppressed" convention
        // cannot tell an empty payload from a suppressed one.
        (StormWebSocketServer server, int port) = await StartServerAsync();
        await using StormWebSocketServer _ = server;

        TaskCompletionSource<int> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        server.OnMessageReceived += (_, msg) =>
        {
            received.TrySetResult(msg.Data.Length);
            return ValueTask.CompletedTask;
        };

        using NetworkStream stream = await HandshakeAsync(port);
        await stream.WriteAsync(MaskedFrame(opCodeByte, []));

        Assert.Equal(0, await received.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task InvalidUtf8TextFrame_FailsConnectionWithInvalidPayload()
    {
        (StormWebSocketServer server, int port) = await StartServerAsync();
        await using StormWebSocketServer _ = server;

        List<string> received = [];
        server.OnMessageReceived += (_, msg) =>
        {
            lock (received) received.Add(msg.Text);
            return ValueTask.CompletedTask;
        };

        using NetworkStream stream = await HandshakeAsync(port);
        await stream.WriteAsync(MaskedFrame(0x81, [0xC3, 0x28, 0xA0, 0xA1, 0xF0, 0x28, 0x8C, 0x28]));

        (int opCode, int closeCode) = await ReadFrameAsync(stream);
        Assert.Equal(0x8, opCode);
        Assert.Equal((int)WsCloseStatus.InvalidPayload, closeCode);

        lock (received)
        {
            Assert.Empty(received);
        }
    }

    [Fact]
    public async Task Utf8CodePointSplitAcrossFragments_IsAccepted()
    {
        (StormWebSocketServer server, int port) = await StartServerAsync();
        await using StormWebSocketServer _ = server;

        TaskCompletionSource<string> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        server.OnMessageReceived += (_, msg) =>
        {
            received.TrySetResult(msg.Text);
            return ValueTask.CompletedTask;
        };

        using NetworkStream stream = await HandshakeAsync(port);

        // "ü" is 0xC3 0xBC — split so the continuation byte lands in the next fragment.
        await stream.WriteAsync(MaskedFrame(0x01, [0x68, 0xC3]));  // Text, FIN=0: "h" + lead byte
        await stream.WriteAsync(MaskedFrame(0x80, [0xBC, 0x69]));  // Continuation, FIN=1: trail byte + "i"

        string text = await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("hüi", text);
    }

    [Fact]
    public async Task Utf8SequenceTruncatedAtFin_FailsConnection()
    {
        (StormWebSocketServer server, int port) = await StartServerAsync();
        await using StormWebSocketServer _ = server;

        using NetworkStream stream = await HandshakeAsync(port);

        await stream.WriteAsync(MaskedFrame(0x01, [0x68, 0xC3]));  // ends mid code point
        await stream.WriteAsync(MaskedFrame(0x80, []));            // FIN with nothing to complete it

        (int opCode, int closeCode) = await ReadFrameAsync(stream);
        Assert.Equal(0x8, opCode);
        Assert.Equal((int)WsCloseStatus.InvalidPayload, closeCode);
    }

    [Fact]
    public async Task FragmentedControlFrame_FailsConnection()
    {
        (StormWebSocketServer server, int port) = await StartServerAsync();
        await using StormWebSocketServer _ = server;

        using NetworkStream stream = await HandshakeAsync(port);

        // RFC 6455 Section 5.5: a Ping with FIN=0 must fail the connection, not be answered.
        await stream.WriteAsync(MaskedFrame(0x09, "hi"u8));

        (int opCode, int closeCode) = await ReadFrameAsync(stream);
        Assert.Equal(0x8, opCode);
        Assert.Equal((int)WsCloseStatus.ProtocolError, closeCode);
    }

    [Theory]
    [InlineData(1005)] // reserved for local reporting only
    [InlineData(1006)]
    [InlineData(1004)] // unassigned
    [InlineData(999)]
    [InlineData(1015)]
    [InlineData(5000)]
    public async Task InvalidCloseCode_IsAnsweredWithProtocolError(int code)
    {
        (StormWebSocketServer server, int port) = await StartServerAsync();
        await using StormWebSocketServer _ = server;

        using NetworkStream stream = await HandshakeAsync(port);
        await stream.WriteAsync(MaskedFrame(0x88, [(byte)(code >> 8), (byte)(code & 0xFF)]));

        (int opCode, int closeCode) = await ReadFrameAsync(stream);
        Assert.Equal(0x8, opCode);
        Assert.Equal((int)WsCloseStatus.ProtocolError, closeCode);
    }

    [Fact]
    public async Task ValidCloseCode_IsEchoedExactlyOnce()
    {
        (StormWebSocketServer server, int port) = await StartServerAsync();
        await using StormWebSocketServer _ = server;

        using NetworkStream stream = await HandshakeAsync(port);
        await stream.WriteAsync(MaskedFrame(0x88, [0x03, 0xE8])); // 1000

        (int opCode, int closeCode) = await ReadFrameAsync(stream);
        Assert.Equal(0x8, opCode);
        Assert.Equal(1000, closeCode);

        // RFC 6455 Section 6.1: nothing may follow the Close frame — the peer must see EOF next.
        byte[] buffer = new byte[64];
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));
        int read = await stream.ReadAsync(buffer, cts.Token);
        Assert.Equal(0, read);
    }

    [Fact]
    public async Task OneBytePayloadCloseFrame_IsAnsweredWithProtocolError()
    {
        (StormWebSocketServer server, int port) = await StartServerAsync();
        await using StormWebSocketServer _ = server;

        using NetworkStream stream = await HandshakeAsync(port);
        await stream.WriteAsync(MaskedFrame(0x88, [0x03]));

        (int opCode, int closeCode) = await ReadFrameAsync(stream);
        Assert.Equal(0x8, opCode);
        Assert.Equal((int)WsCloseStatus.ProtocolError, closeCode);
    }

    [Fact]
    public async Task CloseReasonWithInvalidUtf8_IsAnsweredWithInvalidPayload()
    {
        (StormWebSocketServer server, int port) = await StartServerAsync();
        await using StormWebSocketServer _ = server;

        using NetworkStream stream = await HandshakeAsync(port);
        await stream.WriteAsync(MaskedFrame(0x88, [0x03, 0xE8, 0xC3, 0x28]));

        (int opCode, int closeCode) = await ReadFrameAsync(stream);
        Assert.Equal(0x8, opCode);
        Assert.Equal((int)WsCloseStatus.InvalidPayload, closeCode);
    }

    [Fact]
    public async Task PayloadLengthWithMostSignificantBitSet_FailsCleanly()
    {
        (StormWebSocketServer server, int port) = await StartServerAsync();
        await using StormWebSocketServer _ = server;

        DisconnectReasonRecorder recorder = new();
        server.OnDisconnected += recorder.Record;

        using NetworkStream stream = await HandshakeAsync(port);

        // 64-bit length with the MSB set: must be a protocol error, not an unhandled exception.
        byte[] frame = new byte[2 + 8 + 4];
        frame[0] = 0x82;
        frame[1] = 0x80 | 127;
        frame[2] = 0xFF;
        await stream.WriteAsync(frame);

        (int opCode, int closeCode) = await ReadFrameAsync(stream);
        Assert.Equal(0x8, opCode);
        Assert.Equal((int)WsCloseStatus.ProtocolError, closeCode);

        Assert.Equal(StormSocket.Core.DisconnectReason.ProtocolError, await recorder.WaitAsync());
    }

    [Fact]
    public async Task NonMinimalPayloadLength_FailsConnection()
    {
        (StormWebSocketServer server, int port) = await StartServerAsync();
        await using StormWebSocketServer _ = server;

        using NetworkStream stream = await HandshakeAsync(port);

        // 124 bytes encoded in the 16-bit form instead of the 7-bit form.
        byte[] mask = RandomNumberGenerator.GetBytes(4);
        byte[] frame = new byte[4 + 4 + 124];
        frame[0] = 0x82;
        frame[1] = 0x80 | 126;
        frame[2] = 0x00;
        frame[3] = 124;
        mask.CopyTo(frame.AsSpan(4));
        await stream.WriteAsync(frame);

        (int opCode, int closeCode) = await ReadFrameAsync(stream);
        Assert.Equal(0x8, opCode);
        Assert.Equal((int)WsCloseStatus.ProtocolError, closeCode);
    }

    [Fact]
    public async Task DecompressionBomb_IsRejectedInsteadOfExhaustingMemory()
    {
        (StormWebSocketServer server, int port) = await StartServerAsync(new WebSocketOptions
        {
            Heartbeat = new() { PingInterval = TimeSpan.Zero },
            MaxMessageSize = 64 * 1024,
            Compression = new() { Enabled = true },
        });
        await using StormWebSocketServer _ = server;

        List<int> received = [];
        server.OnMessageReceived += (_, msg) =>
        {
            lock (received) received.Add(msg.Data.Length);
            return ValueTask.CompletedTask;
        };

        using NetworkStream stream = await HandshakeAsync(port, "permessage-deflate; client_no_context_takeover");

        // 4 MB of zeros deflates to a few KB and would inflate far past MaxMessageSize.
        byte[] bomb = Compress(new byte[4 * 1024 * 1024]);
        byte[] mask = RandomNumberGenerator.GetBytes(4);
        byte[] frame = new byte[4 + 4 + bomb.Length];
        frame[0] = 0xC2;                                   // FIN + RSV1 + Binary
        frame[1] = (byte)(0x80 | 126);
        frame[2] = (byte)(bomb.Length >> 8);
        frame[3] = (byte)(bomb.Length & 0xFF);
        mask.CopyTo(frame.AsSpan(4));
        for (int i = 0; i < bomb.Length; i++)
        {
            frame[8 + i] = (byte)(bomb[i] ^ mask[i & 3]);
        }

        await stream.WriteAsync(frame);

        (int opCode, int closeCode) = await ReadFrameAsync(stream);
        Assert.Equal(0x8, opCode);
        Assert.Equal((int)WsCloseStatus.MessageTooBig, closeCode);

        lock (received)
        {
            Assert.Empty(received);
        }
    }

    private static byte[] Compress(byte[] data)
    {
        using MemoryStream output = new();
        using (System.IO.Compression.DeflateStream deflate = new(output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data);
        }

        byte[] result = output.ToArray();

        // permessage-deflate payloads carry no trailing empty block.
        return result.Length >= 4 && result[^4] == 0x00 && result[^3] == 0x00 && result[^2] == 0xFF && result[^1] == 0xFF
            ? result[..^4]
            : result;
    }

    [Theory]
    [InlineData("POST / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n")]
    [InlineData("GET / HTTP/1.0\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: not-base64-at-all\r\nSec-WebSocket-Version: 13\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: c2hvcnQ=\r\nSec-WebSocket-Version: 13\r\n\r\n")]
    public async Task MalformedUpgradeRequest_IsRejected(string requestTemplate)
    {
        (StormWebSocketServer server, int port) = await StartServerAsync();
        await using StormWebSocketServer _ = server;

        using TcpClient tcp = new();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        using NetworkStream stream = tcp.GetStream();

        string request = requestTemplate.Replace("{key}", Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));

        byte[] buffer = new byte[1024];
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));
        int read = await stream.ReadAsync(buffer, cts.Token);
        string response = Encoding.ASCII.GetString(buffer, 0, read);

        Assert.DoesNotContain("101 Switching Protocols", response);
    }

    [Fact]
    public async Task HeaderValueWithBareLineFeed_CannotInjectResponseHeaders()
    {
        (StormWebSocketServer server, int port) = await StartServerAsync(configure: server =>
        {
            server.OnConnecting += context =>
            {
                if (context.RequestedSubprotocols.Count > 0)
                {
                    try
                    {
                        context.AcceptSubprotocol(context.RequestedSubprotocols[0]);
                    }
                    catch (ArgumentException)
                    {
                        context.Reject(400, "Bad subprotocol");
                        return ValueTask.CompletedTask;
                    }
                }

                context.Accept();
                return ValueTask.CompletedTask;
            };
        });
        await using StormWebSocketServer _ = server;

        using TcpClient tcp = new();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        using NetworkStream stream = tcp.GetStream();

        string key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        string request =
            $"GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            $"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n" +
            "Sec-WebSocket-Protocol: chat\nX-Injected: yes\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));

        byte[] buffer = new byte[2048];
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));
        int read = await stream.ReadAsync(buffer, cts.Token);
        string response = Encoding.ASCII.GetString(buffer, 0, read);

        Assert.DoesNotContain("X-Injected", response);
    }

    [Fact]
    public async Task OversizedUpgradeRequest_IsRejectedInsteadOfBufferedForever()
    {
        (StormWebSocketServer server, int port) = await StartServerAsync(new WebSocketOptions
        {
            Heartbeat = new() { PingInterval = TimeSpan.Zero },
            MaxRequestHeaderBytes = 8 * 1024,
            HandshakeTimeout = TimeSpan.FromSeconds(30),
        });
        await using StormWebSocketServer _ = server;

        using TcpClient tcp = new();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        using NetworkStream stream = tcp.GetStream();

        await stream.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\n"));

        byte[] filler = Encoding.ASCII.GetBytes("X-Filler: " + new string('a', 512) + "\r\n");
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        // Well under the handshake timeout: the size cap, not the clock, has to stop this.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            for (int i = 0; i < 200; i++)
            {
                await stream.WriteAsync(filler, cts.Token);
                await stream.FlushAsync(cts.Token);
            }

            byte[] buffer = new byte[1024];
            int read = await stream.ReadAsync(buffer, cts.Token);
            string response = Encoding.ASCII.GetString(buffer, 0, read);
            Assert.Contains("431", response);
            throw new InvalidOperationException("rejected");
        });
    }

    private sealed class DisconnectReasonRecorder
    {
        private readonly TaskCompletionSource<StormSocket.Core.DisconnectReason> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask Record(IWebSocketSession session, StormSocket.Core.DisconnectReason reason)
        {
            _tcs.TrySetResult(reason);
            return ValueTask.CompletedTask;
        }

        public Task<StormSocket.Core.DisconnectReason> WaitAsync() => _tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
