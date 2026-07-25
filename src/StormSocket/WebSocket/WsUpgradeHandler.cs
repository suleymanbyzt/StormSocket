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
    private const int DefaultMaxRequestHeaderBytes = 16 * 1024;
    private const int DefaultMaxRequestHeaderCount = 100;
    private const int WsKeyDecodedLength = 16;
    private const int WsKeyBase64Length = 24;

    /// <summary>
    /// Parses and validates a WebSocket upgrade request per RFC 6455 4.2.1.
    /// Validates: request line, Host, Upgrade, Connection, Sec-WebSocket-Version, Sec-WebSocket-Key,
    /// and optionally Origin headers.
    /// </summary>
    /// <param name="buffer">The request buffer.</param>
    /// <param name="wsKey">The extracted Sec-WebSocket-Key.</param>
    /// <param name="allowedOrigins">Optional list of allowed origins for CSWSH protection (RFC 6455 10.2).</param>
    public static WsUpgradeResult TryParseUpgradeRequest(
        ref ReadOnlySequence<byte> buffer,
        out string? wsKey,
        IReadOnlyList<string>? allowedOrigins = null)
    {
        int scanOffset = 0;
        WsUpgradeResult result = TryParseUpgradeRequest(
            ref buffer,
            ref scanOffset,
            out WsUpgradeContext? context,
            out _,
            remoteEndPoint: null,
            allowedOrigins);

        wsKey = context?.WsKey;
        return result;
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
        int scanOffset = 0;
        return TryParseUpgradeRequest(
            ref buffer,
            ref scanOffset,
            out context,
            out _,
            remoteEndPoint,
            allowedOrigins);
    }

    /// <summary>
    /// Parses a WebSocket upgrade request, enforcing limits on the size and number of request headers
    /// and resuming the search for the end of the header block where the previous read left off.
    /// </summary>
    /// <param name="buffer">
    /// The bytes received so far. Sliced past the request once the handshake has been parsed; left
    /// untouched while the request is still incomplete.
    /// </param>
    /// <param name="scanOffset">
    /// How many bytes of <paramref name="buffer"/> have already been searched for the end of the header
    /// block. Initialize to 0 before the first read and pass the same variable on every subsequent read
    /// of the same connection so a partial request is not rescanned from the start each time.
    /// </param>
    /// <param name="context">The parsed request context; null unless the result is <see cref="WsUpgradeResult.Success"/>.</param>
    /// <param name="errorResponse">
    /// The HTTP response to write before closing the connection, or null when the result is
    /// <see cref="WsUpgradeResult.Success"/> or <see cref="WsUpgradeResult.Incomplete"/>. It carries the
    /// precise status (431, 426, 403, 400) for failures that <see cref="WsUpgradeResult"/> cannot express.
    /// </param>
    /// <param name="remoteEndPoint">The remote endpoint of the connecting client.</param>
    /// <param name="allowedOrigins">Optional list of allowed origins for CSWSH protection (RFC 6455 10.2).</param>
    /// <param name="maxRequestHeaderBytes">Maximum size of the request line plus headers; larger requests are answered with 431.</param>
    /// <param name="maxRequestHeaderCount">Maximum number of header fields; requests with more are answered with 400.</param>
    public static WsUpgradeResult TryParseUpgradeRequest(
        ref ReadOnlySequence<byte> buffer,
        ref int scanOffset,
        out WsUpgradeContext? context,
        out byte[]? errorResponse,
        EndPoint? remoteEndPoint,
        IReadOnlyList<string>? allowedOrigins = null,
        int maxRequestHeaderBytes = DefaultMaxRequestHeaderBytes,
        int maxRequestHeaderCount = DefaultMaxRequestHeaderCount)
    {
        context = null;
        errorResponse = null;

        // Only the first maxRequestHeaderBytes are searched: the rest of the buffer may already hold
        // WebSocket frames the client pipelined behind a perfectly small handshake.
        long searchLimit = Math.Min(buffer.Length, maxRequestHeaderBytes);
        long resumeAt = scanOffset > CrLfCrLf.Length - 1 ? scanOffset - (CrLfCrLf.Length - 1) : 0;
        if (resumeAt > searchLimit)
        {
            resumeAt = searchLimit;
        }

        SequenceReader<byte> reader = new SequenceReader<byte>(buffer.Slice(resumeAt, searchLimit - resumeAt));

        if (!reader.TryReadTo(out ReadOnlySequence<byte> beforeTerminator, CrLfCrLf))
        {
            scanOffset = (int)searchLimit;

            if (buffer.Length < maxRequestHeaderBytes)
            {
                return WsUpgradeResult.Incomplete;
            }

            // A client that never sends the end of the header block would otherwise make the server
            // buffer and rescan without bound, which costs the server far more than it costs the client.
            errorResponse = BuildStatusResponse(431, "Request Header Fields Too Large", "Request header fields too large");
            return WsUpgradeResult.MissingUpgradeHeader;
        }

        long headerBlockLength = resumeAt + beforeTerminator.Length;
        SequencePosition consumed = buffer.GetPosition(headerBlockLength + CrLfCrLf.Length);
        string headerBlock = DecodeAscii(buffer.Slice(0, headerBlockLength));

        WsUpgradeResult result = ValidateRequest(
            headerBlock,
            remoteEndPoint,
            allowedOrigins,
            maxRequestHeaderCount,
            out context,
            out errorResponse);

        buffer = buffer.Slice(consumed);
        return result;
    }

    private static WsUpgradeResult ValidateRequest(
        string headerBlock,
        EndPoint? remoteEndPoint,
        IReadOnlyList<string>? allowedOrigins,
        int maxRequestHeaderCount,
        out WsUpgradeContext? context,
        out byte[]? errorResponse)
    {
        context = null;

        string[] lines = headerBlock.Split("\r\n");

        // RFC 6455 4.2.1: the request must be a GET on HTTP/1.1 or higher.
        string[] requestLine = lines[0].Split(' ');
        if (requestLine.Length is not 3
            || !requestLine[0].Equals("GET", StringComparison.Ordinal)
            || !IsSupportedHttpVersion(requestLine[2])
            || ContainsControlCharacter(lines[0].AsSpan()))
        {
            errorResponse = BuildStatusResponse(400, "Bad Request", "Malformed WebSocket upgrade request line");
            return WsUpgradeResult.MissingUpgradeHeader;
        }

        if (lines.Length - 1 > maxRequestHeaderCount)
        {
            errorResponse = BuildStatusResponse(400, "Bad Request", "Too many request header fields");
            return WsUpgradeResult.MissingUpgradeHeader;
        }

        string path = requestLine[1];
        string? queryString = null;
        int queryIndex = path.IndexOf('?');
        if (queryIndex >= 0)
        {
            queryString = path[(queryIndex + 1)..];
            path = path[..queryIndex];
        }

        Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            int colonIndex = line.IndexOf(':');

            // RFC 7230 3.2.4: no whitespace is allowed before the colon, and a bare CR or LF inside a
            // field value would let the client smuggle an extra header into the 101 response we echo.
            if (colonIndex <= 0 || !IsToken(line.AsSpan(0, colonIndex)) || ContainsControlCharacter(line.AsSpan(colonIndex + 1)))
            {
                errorResponse = BuildStatusResponse(400, "Bad Request", "Malformed request header field");
                return WsUpgradeResult.MissingUpgradeHeader;
            }

            string headerName = line[..colonIndex];
            string headerValue = line[(colonIndex + 1)..].Trim();

            if (headers.TryGetValue(headerName, out string? existingValue))
            {
                // RFC 6455 4.2.1 and RFC 7230 5.4 allow exactly one of these; accepting the last one
                // lets a client hide a second handshake behind the one an intermediary validated.
                if (IsSingleValueHeader(headerName))
                {
                    errorResponse = BuildStatusResponse(400, "Bad Request", $"Duplicate {headerName} header");
                    return WsUpgradeResult.MissingUpgradeHeader;
                }

                headers[headerName] = existingValue.Length is 0 ? headerValue : $"{existingValue}, {headerValue}";
            }
            else
            {
                headers[headerName] = headerValue;
            }
        }

        if (!headers.TryGetValue("Host", out string? host) || host.Length is 0)
        {
            errorResponse = BuildStatusResponse(400, "Bad Request", "Missing Host header");
            return WsUpgradeResult.MissingUpgradeHeader;
        }

        if (!headers.TryGetValue("Upgrade", out string? upgrade) || !ContainsToken(upgrade, "websocket"))
        {
            errorResponse = BuildErrorResponse(WsUpgradeResult.MissingUpgradeHeader);
            return WsUpgradeResult.MissingUpgradeHeader;
        }

        if (!headers.TryGetValue("Connection", out string? connection) || !ContainsToken(connection, "Upgrade"))
        {
            errorResponse = BuildErrorResponse(WsUpgradeResult.MissingConnectionHeader);
            return WsUpgradeResult.MissingConnectionHeader;
        }

        if (!headers.TryGetValue("Sec-WebSocket-Version", out string? version) || version is not "13")
        {
            errorResponse = BuildErrorResponse(WsUpgradeResult.InvalidVersion);
            return WsUpgradeResult.InvalidVersion;
        }

        if (!headers.TryGetValue("Sec-WebSocket-Key", out string? key) || !IsValidWebSocketKey(key))
        {
            errorResponse = BuildErrorResponse(WsUpgradeResult.MissingKey);
            return WsUpgradeResult.MissingKey;
        }

        // RFC 6455 10.2: Origin validation for CSWSH protection
        if (allowedOrigins is { Count: > 0 })
        {
            headers.TryGetValue("Origin", out string? origin);

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
                errorResponse = BuildErrorResponse(WsUpgradeResult.ForbiddenOrigin);
                return WsUpgradeResult.ForbiddenOrigin;
            }
        }

        context = new WsUpgradeContext(path, queryString, headers, key, remoteEndPoint);
        errorResponse = null;
        return WsUpgradeResult.Success;
    }

    public static byte[] BuildUpgradeResponse(string wsKey, string? extensionResponse = null, string? subprotocol = null)
    {
        // Both values end up as header values in the 101 response, so anything that could terminate a
        // header line early must never reach the wire (RFC 7230 3.2.4).
        if (extensionResponse is not null && ContainsControlCharacter(extensionResponse.AsSpan()))
        {
            throw new ArgumentException("Extension response contains control characters.", nameof(extensionResponse));
        }

        if (subprotocol is not null && !IsToken(subprotocol.AsSpan()))
        {
            throw new ArgumentException("Subprotocol is not a valid RFC 6455 token.", nameof(subprotocol));
        }

        string acceptKey = ComputeAcceptKey(wsKey);
        string extensionHeader = extensionResponse is not null
            ? $"Sec-WebSocket-Extensions: {extensionResponse}\r\n"
            : "";
        string subprotocolHeader = subprotocol is not null
            ? $"Sec-WebSocket-Protocol: {subprotocol}\r\n"
            : "";
        string response = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {acceptKey}\r\n{extensionHeader}{subprotocolHeader}\r\n";
        return Encoding.ASCII.GetBytes(response);
    }

    /// <summary>
    /// Builds an appropriate HTTP error response for invalid upgrade requests.
    /// </summary>
    public static byte[] BuildErrorResponse(WsUpgradeResult error) => error switch
    {
        WsUpgradeResult.ForbiddenOrigin => BuildStatusResponse(403, "Forbidden", "Origin not allowed"),

        // RFC 6455 4.4: a version the server cannot speak is answered with 426 plus the version it can.
        WsUpgradeResult.InvalidVersion => BuildStatusResponse(
            426,
            "Upgrade Required",
            "Unsupported WebSocket version",
            "Sec-WebSocket-Version: 13\r\n"),

        WsUpgradeResult.MissingUpgradeHeader => BuildStatusResponse(400, "Bad Request", "Missing or invalid Upgrade header"),
        WsUpgradeResult.MissingConnectionHeader => BuildStatusResponse(400, "Bad Request", "Missing or invalid Connection header"),
        WsUpgradeResult.MissingKey => BuildStatusResponse(400, "Bad Request", "Missing or invalid Sec-WebSocket-Key header"),
        _ => BuildStatusResponse(400, "Bad Request", "Bad Request"),
    };

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
            426 => "Upgrade Required",
            429 => "Too Many Requests",
            431 => "Request Header Fields Too Large",
            _ => "Error",
        };

        return BuildStatusResponse(statusCode, statusText, reason ?? statusText);
    }

    private static byte[] BuildStatusResponse(int statusCode, string statusText, string reason, string? extraHeaders = null)
    {
        // The reason is application supplied and is measured by Content-Length, so control characters
        // are stripped rather than allowed to desynchronize the response.
        string body = SanitizeReason(reason);
        string response = $"HTTP/1.1 {statusCode} {statusText}\r\n{extraHeaders}Content-Type: text/plain\r\nContent-Length: {Encoding.ASCII.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
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
    public static (byte[] Request, string WsKey) BuildUpgradeRequest(Uri uri, IReadOnlyDictionary<string, string>? additionalHeaders = null, string? extensionOffer = null, IReadOnlyList<string>? subprotocols = null)
    {
        byte[] nonce = new byte[WsKeyDecodedLength];
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

        if (extensionOffer is not null)
        {
            sb.Append($"Sec-WebSocket-Extensions: {extensionOffer}\r\n");
        }

        if (subprotocols is { Count: > 0 })
        {
            sb.Append($"Sec-WebSocket-Protocol: {string.Join(", ", subprotocols)}\r\n");
        }

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
        return TryParseUpgradeResponse(ref buffer, expectedWsKey, out _);
    }

    public static bool TryParseUpgradeResponse(ref ReadOnlySequence<byte> buffer, string expectedWsKey, out string? extensions)
    {
        return TryParseUpgradeResponse(ref buffer, expectedWsKey, out extensions, out _);
    }

    public static bool TryParseUpgradeResponse(ref ReadOnlySequence<byte> buffer, string expectedWsKey, out string? extensions, out string? subprotocol)
        => ParseUpgradeResponse(ref buffer, expectedWsKey, out extensions, out subprotocol, out _) == WsUpgradeResponseState.Accepted;

    /// <summary>
    /// Parses the server's handshake response, distinguishing "not all of it has arrived yet" from
    /// "it arrived and the server said no".
    /// </summary>
    /// <remarks>
    /// A plain bool cannot tell those apart, which leaves a client waiting for more bytes that will
    /// never come after a rejection — until its connect timeout fires, turning a clear 401 into a
    /// timeout several seconds later.
    /// </remarks>
    /// <param name="statusLine">The response's status line, available whenever the headers were complete.</param>
    internal static WsUpgradeResponseState ParseUpgradeResponse(
        ref ReadOnlySequence<byte> buffer,
        string expectedWsKey,
        out string? extensions,
        out string? subprotocol,
        out string? statusLine)
    {
        extensions = null;
        subprotocol = null;
        statusLine = null;

        SequenceReader<byte> reader = new SequenceReader<byte>(buffer);
        if (!reader.TryReadTo(out ReadOnlySequence<byte> beforeTerminator, CrLfCrLf))
        {
            return WsUpgradeResponseState.Incomplete;
        }

        string headerStr = DecodeAscii(beforeTerminator);
        buffer = buffer.Slice(reader.Position);

        string[] lines = headerStr.Split("\r\n");
        statusLine = lines.Length > 0 ? lines[0] : null;

        if (lines.Length is 0 || !lines[0].StartsWith("HTTP/1.1 101", StringComparison.Ordinal))
        {
            return WsUpgradeResponseState.Rejected;
        }

        string expectedAccept = ComputeAcceptKey(expectedWsKey);
        bool acceptValid = false;
        foreach (string line in lines)
        {
            if (line.StartsWith("Sec-WebSocket-Accept:", StringComparison.OrdinalIgnoreCase))
            {
                string actual = line.Substring("Sec-WebSocket-Accept:".Length).Trim();
                acceptValid = actual == expectedAccept;
            }
            else if (line.StartsWith("Sec-WebSocket-Extensions:", StringComparison.OrdinalIgnoreCase))
            {
                extensions = line.Substring("Sec-WebSocket-Extensions:".Length).Trim();
            }
            else if (line.StartsWith("Sec-WebSocket-Protocol:", StringComparison.OrdinalIgnoreCase))
            {
                subprotocol = line.Substring("Sec-WebSocket-Protocol:".Length).Trim();
            }
        }

        return acceptValid ? WsUpgradeResponseState.Accepted : WsUpgradeResponseState.InvalidAcceptKey;
    }

    private static string DecodeAscii(in ReadOnlySequence<byte> sequence)
    {
        if (sequence.IsSingleSegment)
        {
            return Encoding.ASCII.GetString(sequence.FirstSpan);
        }

        int length = (int)sequence.Length;
        byte[] rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            sequence.CopyTo(rented);
            return Encoding.ASCII.GetString(rented, 0, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static bool IsSupportedHttpVersion(string version)
    {
        if (!version.StartsWith("HTTP/", StringComparison.Ordinal))
        {
            return false;
        }

        int dotIndex = version.IndexOf('.', "HTTP/".Length);
        if (dotIndex < 0
            || !int.TryParse(version.AsSpan("HTTP/".Length, dotIndex - "HTTP/".Length), out int major)
            || !int.TryParse(version.AsSpan(dotIndex + 1), out int minor))
        {
            return false;
        }

        return major > 1 || (major is 1 && minor >= 1);
    }

    private static bool IsValidWebSocketKey(string key)
    {
        if (key.Length is not WsKeyBase64Length)
        {
            return false;
        }

        Span<byte> decoded = stackalloc byte[WsKeyDecodedLength];
        return Convert.TryFromBase64String(key, decoded, out int written) && written is WsKeyDecodedLength;
    }

    private static bool IsSingleValueHeader(string headerName) =>
        headerName.Equals("Host", StringComparison.OrdinalIgnoreCase)
        || headerName.Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase)
        || headerName.Equals("Sec-WebSocket-Version", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Tests whether a comma-separated header value carries <paramref name="token"/> as one of its
    /// elements. Substring matching would accept values such as "not-websocket".
    /// </summary>
    private static bool ContainsToken(string headerValue, string token)
    {
        foreach (string element in headerValue.Split(',', StringSplitOptions.TrimEntries))
        {
            if (element.Equals(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsToken(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return false;
        }

        foreach (char c in value)
        {
            bool isTokenChar = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
                or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';

            if (!isTokenChar)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsControlCharacter(ReadOnlySpan<char> value)
    {
        foreach (char c in value)
        {
            if ((c < ' ' && c is not '\t') || c is (char)0x7F)
            {
                return true;
            }
        }

        return false;
    }

    private static string SanitizeReason(string reason)
    {
        if (!ContainsControlCharacter(reason.AsSpan()))
        {
            return reason;
        }

        char[] sanitized = reason.ToCharArray();
        for (int i = 0; i < sanitized.Length; i++)
        {
            if (sanitized[i] < ' ' || sanitized[i] is (char)0x7F)
            {
                sanitized[i] = ' ';
            }
        }

        return new string(sanitized);
    }
}
