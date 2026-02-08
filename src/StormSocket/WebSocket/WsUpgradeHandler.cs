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

    /// <summary>
    /// Builds an HTTP/1.1 WebSocket upgrade request for the client.
    /// Returns the request bytes and the generated Sec-WebSocket-Key (needed to validate the server response).
    /// </summary>
    public static (byte[] Request, string WsKey) BuildUpgradeRequest(Uri uri, IReadOnlyDictionary<string, string>? additionalHeaders = null)
    {
        byte[] nonce = new byte[16];
        RandomNumberGenerator.Fill(nonce);
        string wsKey = Convert.ToBase64String(nonce);

        string host = uri.Port is 80 or 443
            ? uri.Host
            : $"{uri.Host}:{uri.Port}";

        string path = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;

        StringBuilder sb = new StringBuilder();
        sb.Append($"GET {path} HTTP/1.1\r\n");
        sb.Append($"Host: {host}\r\n");
        sb.Append("Upgrade: websocket\r\n");
        sb.Append("Connection: Upgrade\r\n");
        sb.Append($"Sec-WebSocket-Key: {wsKey}\r\n");
        sb.Append("Sec-WebSocket-Version: 13\r\n");

        if (additionalHeaders is not null)
        {
            foreach (KeyValuePair<string, string> kvp in additionalHeaders)
            {
                sb.Append($"{kvp.Key}: {kvp.Value}\r\n");
            }
        }

        sb.Append("\r\n");
        return (Encoding.ASCII.GetBytes(sb.ToString()), wsKey);
    }

    /// <summary>
    /// Parses the server's HTTP/1.1 101 Switching Protocols response and validates Sec-WebSocket-Accept.
    /// </summary>
    public static bool TryParseUpgradeResponse(ref ReadOnlySequence<byte> buffer, string expectedWsKey)
    {
        Span<byte> headerEndSpan = CrLfCrLf.AsSpan();

        ReadOnlySpan<byte> headerBytes;
        int endIdx;

        if (buffer.IsSingleSegment)
        {
            ReadOnlySpan<byte> span = buffer.FirstSpan;
            endIdx = IndexOf(span, headerEndSpan);
            if (endIdx < 0)
            {
                return false;
            }

            headerBytes = span.Slice(0, endIdx);
        }
        else
        {
            byte[] arr = buffer.ToArray();
            endIdx = IndexOf(arr.AsSpan(), headerEndSpan);
            if (endIdx < 0)
            {
                return false;
            }

            headerBytes = arr.AsSpan(0, endIdx);
        }

        buffer = buffer.Slice(endIdx + 4);

        string headerStr = Encoding.ASCII.GetString(headerBytes);
        string[] lines = headerStr.Split("\r\n");

        if (lines.Length == 0 || !lines[0].StartsWith("HTTP/1.1 101", StringComparison.Ordinal))
        {
            return false;
        }

        // note here.. refactor this
        string expectedAccept = ComputeAcceptKey(expectedWsKey);
        foreach (string line in lines)
        {
            if (line.StartsWith("Sec-WebSocket-Accept:", StringComparison.OrdinalIgnoreCase))
            {
                string actual = line.Substring("Sec-WebSocket-Accept:".Length).Trim();
                return actual == expectedAccept;
            }
        }

        return false;
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