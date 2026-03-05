using System.IO.Compression;

namespace StormSocket.WebSocket;

/// <summary>
/// Per-connection permessage-deflate state (RFC 7692).
/// Handles compression, decompression, and extension negotiation.
/// </summary>
internal sealed class WsPerMessageDeflate : IDisposable
{
    private static readonly byte[] DeflateTrailer = [0x00, 0x00, 0xFF, 0xFF];

    private readonly CompressionLevel _compressionLevel;
    private readonly int _minMessageSize;
    private readonly bool _compressNoContextTakeover;
    private readonly bool _decompressNoContextTakeover;

    private MemoryStream? _compressStream;
    private DeflateStream? _compressor;
    private MemoryStream? _decompressStream;
    private DeflateStream? _decompressor;
    private bool _disposed;

    public WsPerMessageDeflate(
        CompressionLevel compressionLevel,
        int minMessageSize,
        bool compressNoContextTakeover,
        bool decompressNoContextTakeover)
    {
        _compressionLevel = compressionLevel;
        _minMessageSize = minMessageSize;
        _compressNoContextTakeover = compressNoContextTakeover;
        _decompressNoContextTakeover = decompressNoContextTakeover;
    }

    /// <summary>
    /// Returns true if the payload should be compressed based on size threshold.
    /// </summary>
    public bool ShouldCompress(int payloadLength) => payloadLength >= _minMessageSize;

    /// <summary>
    /// Compresses a message payload using DEFLATE. Strips the trailing 0x00 0x00 0xFF 0xFF per RFC 7692.
    /// </summary>
    public byte[] Compress(ReadOnlySpan<byte> payload)
    {
        if (_compressNoContextTakeover || _compressor is null)
        {
            _compressor?.Dispose();
            _compressStream?.Dispose();
            _compressStream = new MemoryStream();
            _compressor = new DeflateStream(_compressStream, _compressionLevel, leaveOpen: true);
        }
        else
        {
            _compressStream!.SetLength(0);
        }

        _compressor!.Write(payload);
        _compressor.Flush();

        byte[] result = _compressStream!.ToArray();

        // Strip the trailing 0x00 0x00 0xFF 0xFF (empty DEFLATE block with BFINAL=0)
        if (result.Length >= 4 &&
            result[^4] == 0x00 && result[^3] == 0x00 &&
            result[^2] == 0xFF && result[^1] == 0xFF)
        {
            byte[] trimmed = new byte[result.Length - 4];
            Buffer.BlockCopy(result, 0, trimmed, 0, trimmed.Length);
            return trimmed;
        }

        return result;
    }

    /// <summary>
    /// Decompresses a message payload. Appends the trailing 0x00 0x00 0xFF 0xFF before decompressing per RFC 7692.
    /// </summary>
    public byte[] Decompress(ReadOnlySpan<byte> compressedPayload)
    {
        if (_decompressNoContextTakeover || _decompressor is null)
        {
            _decompressStream?.Dispose();
            _decompressor?.Dispose();

            // Build input: compressed data + trailer
            byte[] input = new byte[compressedPayload.Length + DeflateTrailer.Length];
            compressedPayload.CopyTo(input);
            DeflateTrailer.CopyTo(input.AsSpan(compressedPayload.Length));

            _decompressStream = new MemoryStream(input);
            _decompressor = new DeflateStream(_decompressStream, CompressionMode.Decompress, leaveOpen: true);
        }
        else
        {
            // Context takeover: append new data + trailer to existing stream
            byte[] input = new byte[compressedPayload.Length + DeflateTrailer.Length];
            compressedPayload.CopyTo(input);
            DeflateTrailer.CopyTo(input.AsSpan(compressedPayload.Length));

            _decompressStream!.SetLength(0);
            _decompressStream.Write(input);
            _decompressStream.Position = 0;
        }

        using MemoryStream output = new();
        _decompressor!.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>
    /// Builds the Sec-WebSocket-Extensions offer header value for the client request.
    /// </summary>
    public static string BuildOfferHeader(WsCompressionOptions options)
    {
        List<string> parts = ["permessage-deflate"];

        if (options.ServerNoContextTakeover)
        {
            parts.Add("server_no_context_takeover");
        }

        if (options.ClientNoContextTakeover)
        {
            parts.Add("client_no_context_takeover");
        }

        if (options.ServerMaxWindowBits < 15)
        {
            parts.Add($"server_max_window_bits={options.ServerMaxWindowBits}");
        }

        if (options.ClientMaxWindowBits < 15)
        {
            parts.Add($"client_max_window_bits={options.ClientMaxWindowBits}");
        }

        return string.Join("; ", parts);
    }

    /// <summary>
    /// Server-side: Tries to negotiate permessage-deflate from the client's offer.
    /// Returns null if negotiation fails or compression is disabled.
    /// </summary>
    public static (WsPerMessageDeflate? deflate, string? responseHeader) TryNegotiate(
        string? clientOffer, WsCompressionOptions serverOptions)
    {
        if (!serverOptions.Enabled || string.IsNullOrEmpty(clientOffer))
        {
            return (null, null);
        }

        // Check if client offers permessage-deflate
        if (!clientOffer.Contains("permessage-deflate", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        // Parse client parameters
        bool clientWantsServerNoContext = clientOffer.Contains("server_no_context_takeover", StringComparison.OrdinalIgnoreCase);
        bool clientWantsClientNoContext = clientOffer.Contains("client_no_context_takeover", StringComparison.OrdinalIgnoreCase);

        // Server decides: use no_context_takeover if either side requests it
        bool serverNoContext = serverOptions.ServerNoContextTakeover || clientWantsServerNoContext;
        bool clientNoContext = serverOptions.ClientNoContextTakeover || clientWantsClientNoContext;

        // Build response
        List<string> responseParts = ["permessage-deflate"];
        if (serverNoContext)
        {
            responseParts.Add("server_no_context_takeover");
        }

        if (clientNoContext)
        {
            responseParts.Add("client_no_context_takeover");
        }

        if (serverOptions.ServerMaxWindowBits < 15)
        {
            responseParts.Add($"server_max_window_bits={serverOptions.ServerMaxWindowBits}");
        }

        if (serverOptions.ClientMaxWindowBits < 15)
        {
            responseParts.Add($"client_max_window_bits={serverOptions.ClientMaxWindowBits}");
        }

        string responseHeader = string.Join("; ", responseParts);

        // Server compresses with server params, decompresses with client params
        WsPerMessageDeflate deflate = new(
            serverOptions.CompressionLevel,
            serverOptions.MinMessageSize,
            compressNoContextTakeover: serverNoContext,
            decompressNoContextTakeover: clientNoContext);

        return (deflate, responseHeader);
    }

    /// <summary>
    /// Client-side: Parses the server's extension response and creates the compression context.
    /// Returns null if the server did not accept the extension.
    /// </summary>
    public static WsPerMessageDeflate? ParseServerResponse(string? serverResponse, WsCompressionOptions clientOptions)
    {
        if (string.IsNullOrEmpty(serverResponse))
        {
            return null;
        }

        if (!serverResponse.Contains("permessage-deflate", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        bool serverNoContext = serverResponse.Contains("server_no_context_takeover", StringComparison.OrdinalIgnoreCase);
        bool clientNoContext = serverResponse.Contains("client_no_context_takeover", StringComparison.OrdinalIgnoreCase);

        // Client compresses with client params, decompresses with server params
        return new WsPerMessageDeflate(
            clientOptions.CompressionLevel,
            clientOptions.MinMessageSize,
            compressNoContextTakeover: clientNoContext,
            decompressNoContextTakeover: serverNoContext);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        
        _disposed = true;

        _compressor?.Dispose();
        _compressStream?.Dispose();
        _decompressor?.Dispose();
        _decompressStream?.Dispose();
    }
}