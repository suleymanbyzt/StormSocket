using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace StormSocket.WebSocket;

/// <summary>
/// Minimal HTTP/1.1 WebSocket upgrade handler (RFC 6455).
/// </summary>
public static class WsUpgradeHandler
{
    private static readonly byte[] CrLfCrLf = "\r\n\r\n"u8.ToArray();
    private const string WsGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    public static bool TryParseUpgradeRequest(ref ReadOnlySequence<byte> buffer, out string? wsKey)
    {
        wsKey = null;

        Span<byte> headerEndSpan = CrLfCrLf.AsSpan();

        ReadOnlySpan<byte> headerBytes;
        if (buffer.IsSingleSegment)
        {
            ReadOnlySpan<byte> span = buffer.FirstSpan;
            
            int idx = IndexOf(span, headerEndSpan);
            if (idx < 0)
            {
                return false;
            }
            
            headerBytes = span.Slice(0, idx);
            buffer = buffer.Slice(idx + 4);
        }
        else
        {
            byte[] arr = buffer.ToArray();
            
            int idx = IndexOf(arr.AsSpan(), headerEndSpan);
            if (idx < 0)
            {
                return false;
            }
            
            headerBytes = arr.AsSpan(0, idx);
            buffer = buffer.Slice(idx + 4);
        }

        string headerStr = Encoding.ASCII.GetString(headerBytes);
        string[] lines = headerStr.Split("\r\n");

        foreach (string line in lines)
        {
            if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
            {
                wsKey = line.Substring("Sec-WebSocket-Key:".Length).Trim();
                break;
            }
        }

        return wsKey != null;
    }

    public static byte[] BuildUpgradeResponse(string wsKey)
    {
        string acceptKey = ComputeAcceptKey(wsKey);
        string response = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {acceptKey}\r\n\r\n";
        return Encoding.ASCII.GetBytes(response);
    }

    private static string ComputeAcceptKey(string wsKey)
    {
        string combined = wsKey + WsGuid;
        byte[] hash = SHA1.HashData(Encoding.ASCII.GetBytes(combined));
        return Convert.ToBase64String(hash);
    }

    private static int IndexOf(ReadOnlySpan<byte> source, ReadOnlySpan<byte> pattern)
    {
        for (int i = 0; i <= source.Length - pattern.Length; i++)
        {
            if (source.Slice(i, pattern.Length).SequenceEqual(pattern))
            {
                return i;
            }
        }
        return -1;
    }
}