using System.Buffers;
using System.Net;
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

    /// <summary>
    /// Parses and validates a WebSocket upgrade request per RFC 6455 4.2.1.
    /// Validates: Upgrade, Connection, Sec-WebSocket-Version, Sec-WebSocket-Key, and optionally Origin headers.
    /// </summary>
    /// <param name="buffer">The request buffer.</param>
    /// <param name="wsKey">The extracted Sec-WebSocket-Key.</param>
    /// <param name="allowedOrigins">Optional list of allowed origins for CSWSH protection (RFC 6455 10.2).</param>
    public static WsUpgradeResult TryParseUpgradeRequest(
        ref ReadOnlySequence<byte> buffer,
        out string? wsKey,
        IReadOnlyList<string>? allowedOrigins = null)
    {
        wsKey = null;

        Span<byte> headerEndSpan = CrLfCrLf.AsSpan();

        ReadOnlySpan<byte> headerBytes;
        SequencePosition consumed;

        if (buffer.IsSingleSegment)
        {
            ReadOnlySpan<byte> span = buffer.FirstSpan;

            int idx = IndexOf(span, headerEndSpan);
            if (idx < 0)
            {
                return WsUpgradeResult.Incomplete;
            }

            headerBytes = span.Slice(0, idx);
            consumed = buffer.GetPosition(idx + 4);
        }
        else
        {
            byte[] arr = buffer.ToArray();

            int idx = IndexOf(arr.AsSpan(), headerEndSpan);
            if (idx < 0)
            {
                return WsUpgradeResult.Incomplete;
            }

            headerBytes = arr.AsSpan(0, idx);
            consumed = buffer.GetPosition(idx + 4);
        }

        string headerStr = Encoding.ASCII.GetString(headerBytes);
        string[] lines = headerStr.Split("\r\n");

        bool hasUpgrade = false;
        bool hasConnection = false;
        bool hasValidVersion = false;
        string? key = null;
        string? origin = null;

        foreach (string line in lines)
        {
            if (line.StartsWith("Upgrade:", StringComparison.OrdinalIgnoreCase))
            {
                string value = line.Substring("Upgrade:".Length).Trim();
                hasUpgrade = value.Equals("websocket", StringComparison.OrdinalIgnoreCase);
            }
            else if (line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase))
            {
                string value = line.Substring("Connection:".Length).Trim();
                hasConnection = value.Contains("Upgrade", StringComparison.OrdinalIgnoreCase);
            }
            else if (line.StartsWith("Sec-WebSocket-Version:", StringComparison.OrdinalIgnoreCase))
            {
                string value = line.Substring("Sec-WebSocket-Version:".Length).Trim();
                hasValidVersion = value == "13";
            }
            else if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
            {
                key = line.Substring("Sec-WebSocket-Key:".Length).Trim();
            }
            else if (line.StartsWith("Origin:", StringComparison.OrdinalIgnoreCase))
            {
                origin = line.Substring("Origin:".Length).Trim();
            }
        }

        if (!hasUpgrade)
        {
            buffer = buffer.Slice(consumed);
            return WsUpgradeResult.MissingUpgradeHeader;
        }

        if (!hasConnection)
        {
            buffer = buffer.Slice(consumed);
            return WsUpgradeResult.MissingConnectionHeader;
        }

        if (!hasValidVersion)
        {
            buffer = buffer.Slice(consumed);
            return WsUpgradeResult.InvalidVersion;
        }

        if (string.IsNullOrEmpty(key))
        {
            buffer = buffer.Slice(consumed);
            return WsUpgradeResult.MissingKey;
        }

        // RFC 6455 10.2: Origin validation for CSWSH protection
        if (allowedOrigins is { Count: > 0 })
        {
            bool originAllowed = false;
            foreach (string allowed in allowedOrigins)
            {
                if (string.Equals(origin, allowed, StringComparison.OrdinalIgnoreCase))
                {
                    originAllowed = true;
                    break;
                }
            }

            if (!originAllowed)
            {
                buffer = buffer.Slice(consumed);
                return WsUpgradeResult.ForbiddenOrigin;
            }
        }

        wsKey = key;
        buffer = buffer.Slice(consumed);
        return WsUpgradeResult.Success;
    }

    /// <summary>
    /// Parses a WebSocket upgrade request and returns a context with full request details.
    /// Use this overload when you need access to path, query string, and all headers for authentication.
    /// </summary>
    public static WsUpgradeResult TryParseUpgradeRequest(
        ref ReadOnlySequence<byte> buffer,
        out WsUpgradeContext? context,
        EndPoint? remoteEndPoint,
        IReadOnlyList<string>? allowedOrigins = null)
    {
        context = null;

        Span<byte> headerEndSpan = CrLfCrLf.AsSpan();

        ReadOnlySpan<byte> headerBytes;
        SequencePosition consumed;

        if (buffer.IsSingleSegment)
        {
            ReadOnlySpan<byte> span = buffer.FirstSpan;

            int idx = IndexOf(span, headerEndSpan);
            if (idx < 0)
            {
                return WsUpgradeResult.Incomplete;
            }

            headerBytes = span.Slice(0, idx);
            consumed = buffer.GetPosition(idx + 4);
        }
        else
        {
            byte[] arr = buffer.ToArray();

            int idx = IndexOf(arr.AsSpan(), headerEndSpan);
            if (idx < 0)
            {
                return WsUpgradeResult.Incomplete;
            }

            headerBytes = arr.AsSpan(0, idx);
            consumed = buffer.GetPosition(idx + 4);
        }

        string headerStr = Encoding.ASCII.GetString(headerBytes);
        string[] lines = headerStr.Split("\r\n");

        string path = "/";
        string? queryString = null;

        if (lines.Length > 0)
        {
            string[] requestLine = lines[0].Split(' ');
            if (requestLine.Length >= 2)
            {
                string fullPath = requestLine[1];
                int queryIndex = fullPath.IndexOf('?');
                if (queryIndex >= 0)
                {
                    path = fullPath[..queryIndex];
                    queryString = fullPath[(queryIndex + 1)..];
                }
                else
                {
                    path = fullPath;
                }
            }
        }

        Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool hasUpgrade = false;
        bool hasConnection = false;
        bool hasValidVersion = false;
        string? key = null;
        string? origin = null;

        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            int colonIndex = line.IndexOf(':');
            if (colonIndex <= 0) continue;

            string headerName = line[..colonIndex].Trim();
            string headerValue = line[(colonIndex + 1)..].Trim();
            headers[headerName] = headerValue;

            if (headerName.Equals("Upgrade", StringComparison.OrdinalIgnoreCase))
            {
                hasUpgrade = headerValue.Equals("websocket", StringComparison.OrdinalIgnoreCase);
            }
            else if (headerName.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                hasConnection = headerValue.Contains("Upgrade", StringComparison.OrdinalIgnoreCase);
            }
            else if (headerName.Equals("Sec-WebSocket-Version", StringComparison.OrdinalIgnoreCase))
            {
                hasValidVersion = headerValue == "13";
            }
            else if (headerName.Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
            {
                key = headerValue;
            }
            else if (headerName.Equals("Origin", StringComparison.OrdinalIgnoreCase))
            {
                origin = headerValue;
            }
        }

        if (!hasUpgrade)
        {
            buffer = buffer.Slice(consumed);
            return WsUpgradeResult.MissingUpgradeHeader;
        }

        if (!hasConnection)
        {
            buffer = buffer.Slice(consumed);
            return WsUpgradeResult.MissingConnectionHeader;
        }

        if (!hasValidVersion)
        {
            buffer = buffer.Slice(consumed);
            return WsUpgradeResult.InvalidVersion;
        }

        if (string.IsNullOrEmpty(key))
        {
            buffer = buffer.Slice(consumed);
            return WsUpgradeResult.MissingKey;
        }

        if (allowedOrigins is { Count: > 0 })
        {
            bool originAllowed = false;
            foreach (string allowed in allowedOrigins)
            {
                if (string.Equals(origin, allowed, StringComparison.OrdinalIgnoreCase))
                {
                    originAllowed = true;
                    break;
                }
            }

            if (!originAllowed)
            {
                buffer = buffer.Slice(consumed);
                return WsUpgradeResult.ForbiddenOrigin;
            }
        }

        context = new WsUpgradeContext(path, queryString, headers, key, remoteEndPoint);
        buffer = buffer.Slice(consumed);
        return WsUpgradeResult.Success;
    }

    public static byte[] BuildUpgradeResponse(string wsKey)
    {
        string acceptKey = ComputeAcceptKey(wsKey);
        string response = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {acceptKey}\r\n\r\n";
        return Encoding.ASCII.GetBytes(response);
    }

    /// <summary>
    /// Builds an appropriate HTTP error response for invalid upgrade requests.
    /// </summary>
    public static byte[] BuildErrorResponse(WsUpgradeResult error)
    {
        if (error == WsUpgradeResult.ForbiddenOrigin)
        {
            const string reason = "Origin not allowed";
            string response = $"HTTP/1.1 403 Forbidden\r\nContent-Type: text/plain\r\nContent-Length: {reason.Length}\r\nConnection: close\r\n\r\n{reason}";
            return Encoding.ASCII.GetBytes(response);
        }

        string errorReason = error switch
        {
            WsUpgradeResult.MissingUpgradeHeader => "Missing or invalid Upgrade header",
            WsUpgradeResult.MissingConnectionHeader => "Missing or invalid Connection header",
            WsUpgradeResult.MissingKey => "Missing Sec-WebSocket-Key header",
            WsUpgradeResult.InvalidVersion => "Unsupported WebSocket version",
            _ => "Bad Request",
        };

        string versionHeader = error == WsUpgradeResult.InvalidVersion
            ? "Sec-WebSocket-Version: 13\r\n"
            : "";

        string errorResponse = $"HTTP/1.1 400 Bad Request\r\n{versionHeader}Content-Type: text/plain\r\nContent-Length: {errorReason.Length}\r\nConnection: close\r\n\r\n{errorReason}";
        return Encoding.ASCII.GetBytes(errorResponse);
    }

    /// <summary>
    /// Builds a custom HTTP error response for rejected upgrade requests.
    /// </summary>
    public static byte[] BuildRejectResponse(int statusCode, string? reason = null)
    {
        string statusText = statusCode switch
        {
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            429 => "Too Many Requests",
            _ => "Error",
        };

        reason ??= statusText;
        string response = $"HTTP/1.1 {statusCode} {statusText}\r\nContent-Type: text/plain\r\nContent-Length: {reason.Length}\r\nConnection: close\r\n\r\n{reason}";
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