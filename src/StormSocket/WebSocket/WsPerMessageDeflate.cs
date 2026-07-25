using System.Buffers;
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
    /// <param name="compressedPayload">The compressed message payload (without the DEFLATE trailer).</param>
    /// <param name="maxOutputSize">
    /// Hard cap on the inflated size. DEFLATE reaches ratios above 1000:1, so an attacker-controlled
    /// frame that passes <c>MaxFrameSize</c> can still inflate to gigabytes. Inflation stops and the
    /// connection is failed with 1009 as soon as this limit is exceeded.
    /// </param>
    /// <exception cref="WsProtocolException">The inflated payload exceeds <paramref name="maxOutputSize"/>.</exception>
    public byte[] Decompress(ReadOnlySpan<byte> compressedPayload, int maxOutputSize)
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

        // Inflate incrementally into a pooled buffer so the output size is bounded at all times.
        // The buffer is allowed to reach maxOutputSize + 1 bytes: that extra byte is what proves
        // the peer went over the limit rather than landing exactly on it.
        long ceiling = Math.Min((long)maxOutputSize + 1, Array.MaxLength);
        int capacity = (int)Math.Min(ceiling, Math.Max(4096L, (long)compressedPayload.Length * 4));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(capacity);
        int total = 0;

        try
        {
            while (true)
            {
                if (total == buffer.Length)
                {
                    int grown = (int)Math.Min(ceiling, (long)buffer.Length * 2);
                    if (grown <= buffer.Length)
                    {
                        throw new WsProtocolException(WsCloseStatus.MessageTooBig,
                            $"Decompressed message exceeds maximum size of {maxOutputSize} bytes.");
                    }

                    byte[] larger = ArrayPool<byte>.Shared.Rent(grown);
                    buffer.AsSpan(0, total).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                }

                int read = _decompressor!.Read(buffer, total, buffer.Length - total);
                if (read == 0)
                {
                    break;
                }

                total += read;

                if (total > maxOutputSize)
                {
                    throw new WsProtocolException(WsCloseStatus.MessageTooBig,
                        $"Decompressed message exceeds maximum size of {maxOutputSize} bytes.");
                }
            }

            byte[] result = new byte[total];
            buffer.AsSpan(0, total).CopyTo(result);
            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
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

        // Window-size parameters are deliberately not offered: DeflateStream always uses the full
        // 15-bit window, so advertising anything smaller would be a promise we cannot keep.
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

        // RFC 7692 Section 5.1: the client may send several comma-separated offers in preference
        // order. Accept the first one whose parameters we can actually honor and decline the rest.
        foreach (string candidate in clientOffer.Split(','))
        {
            if (!DeflateOffer.TryParse(candidate, out DeflateOffer offer))
            {
                // Unknown extension or a parameter we do not implement — RFC 7692 Section 7.1
                // requires declining the offer instead of accepting it with different semantics.
                continue;
            }

            // DeflateStream gives no control over the LZ77 window size, so an offer that asks the
            // server to compress with fewer than 15 bits cannot be honored and must be declined.
            if (offer.ServerMaxWindowBits is int serverBits && serverBits < 15)
            {
                continue;
            }

            // Server decides: use no_context_takeover if either side requests it
            bool serverNoContext = serverOptions.ServerNoContextTakeover || offer.ServerNoContextTakeover;
            bool clientNoContext = serverOptions.ClientNoContextTakeover || offer.ClientNoContextTakeover;

            List<string> responseParts = ["permessage-deflate"];
            if (serverNoContext)
            {
                responseParts.Add("server_no_context_takeover");
            }

            if (clientNoContext)
            {
                responseParts.Add("client_no_context_takeover");
            }

            // RFC 7692 Section 7.1.2.1: the server MUST NOT include client_max_window_bits in the
            // response unless the client offered it. Our inflater uses the maximum window, so any
            // value the client is willing to accept is safe to confirm.
            if (offer.ClientMaxWindowBitsOffered)
            {
                int clientBits = Math.Min(offer.ClientMaxWindowBits ?? 15, serverOptions.ClientMaxWindowBits);
                if (clientBits < 15)
                {
                    responseParts.Add($"client_max_window_bits={clientBits}");
                }
            }

            WsPerMessageDeflate deflate = new(
                serverOptions.CompressionLevel,
                serverOptions.MinMessageSize,
                compressNoContextTakeover: serverNoContext,
                decompressNoContextTakeover: clientNoContext);

            return (deflate, string.Join("; ", responseParts));
        }

        return (null, null);
    }

    /// <summary>
    /// One parsed <c>permessage-deflate</c> extension offer (RFC 7692 Section 7.1).
    /// </summary>
    private readonly struct DeflateOffer
    {
        public bool ServerNoContextTakeover { get; private init; }
        public bool ClientNoContextTakeover { get; private init; }
        public int? ServerMaxWindowBits { get; private init; }
        public int? ClientMaxWindowBits { get; private init; }

        /// <summary>True when the client sent client_max_window_bits, with or without a value.</summary>
        public bool ClientMaxWindowBitsOffered { get; private init; }

        /// <summary>
        /// Parses a single extension offer. Returns false for a different extension, a malformed
        /// parameter, a duplicate parameter, or a window size outside the 8-15 range the RFC allows.
        /// </summary>
        public static bool TryParse(string candidate, out DeflateOffer offer)
        {
            offer = default;

            string[] tokens = candidate.Split(';');
            if (!tokens[0].Trim().Equals("permessage-deflate", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            bool serverNoContext = false, clientNoContext = false, clientBitsOffered = false;
            int? serverBits = null, clientBits = null;

            for (int i = 1; i < tokens.Length; i++)
            {
                string token = tokens[i].Trim();
                if (token.Length == 0)
                {
                    continue;
                }

                int eq = token.IndexOf('=');
                string name = (eq < 0 ? token : token[..eq]).Trim();
                string? value = eq < 0 ? null : token[(eq + 1)..].Trim().Trim('"');

                switch (name.ToLowerInvariant())
                {
                    case "server_no_context_takeover":
                        if (value is not null || serverNoContext) return false;
                        serverNoContext = true;
                        break;

                    case "client_no_context_takeover":
                        if (value is not null || clientNoContext) return false;
                        clientNoContext = true;
                        break;

                    case "server_max_window_bits":
                        // RFC 7692 Section 7.1.2.2: in a client offer this parameter must carry a value.
                        if (serverBits is not null || !TryParseWindowBits(value, out int parsedServerBits)) return false;
                        serverBits = parsedServerBits;
                        break;

                    case "client_max_window_bits":
                        // RFC 7692 Section 7.1.2.1: valid with or without a value in a client offer.
                        if (clientBitsOffered) return false;
                        clientBitsOffered = true;
                        if (value is not null)
                        {
                            if (!TryParseWindowBits(value, out int parsedClientBits)) return false;
                            clientBits = parsedClientBits;
                        }

                        break;

                    default:
                        // Unknown parameter — the offer must be declined.
                        return false;
                }
            }

            offer = new DeflateOffer
            {
                ServerNoContextTakeover = serverNoContext,
                ClientNoContextTakeover = clientNoContext,
                ServerMaxWindowBits = serverBits,
                ClientMaxWindowBits = clientBits,
                ClientMaxWindowBitsOffered = clientBitsOffered,
            };

            return true;
        }

        private static bool TryParseWindowBits(string? value, out int bits)
        {
            bits = 0;
            return value is not null && int.TryParse(value, out bits) && bits is >= 8 and <= 15;
        }
    }

    /// <summary>
    /// Client-side: Parses the server's extension response and creates the compression context.
    /// Returns null if the server did not accept the extension.
    /// </summary>
    /// <exception cref="WsProtocolException">
    /// The server response is malformed, contains more than one extension, or requests terms the
    /// client cannot honor. RFC 6455 Section 4.1 requires the client to fail the connection in that case.
    /// </exception>
    public static WsPerMessageDeflate? ParseServerResponse(string? serverResponse, WsCompressionOptions clientOptions)
    {
        if (string.IsNullOrEmpty(serverResponse))
        {
            return null;
        }

        // The server may confirm at most one extension, and only one the client offered.
        string[] responses = serverResponse.Split(',');
        if (responses.Length > 1)
        {
            throw new WsProtocolException(WsCloseStatus.ProtocolError,
                "Server confirmed more than one extension.");
        }

        if (!DeflateOffer.TryParse(responses[0], out DeflateOffer response))
        {
            throw new WsProtocolException(WsCloseStatus.ProtocolError,
                $"Server returned an unsupported or malformed extension: '{serverResponse}'.");
        }

        if (!clientOptions.Enabled)
        {
            throw new WsProtocolException(WsCloseStatus.ProtocolError,
                "Server confirmed permessage-deflate although the client did not offer it.");
        }

        // Our deflater always uses the maximum window, so a server that asks the client to compress
        // with a smaller one is asking for something we cannot deliver.
        if (response.ClientMaxWindowBits is int clientBits && clientBits < 15)
        {
            throw new WsProtocolException(WsCloseStatus.ProtocolError,
                $"Server requested client_max_window_bits={clientBits}, which this client cannot honor.");
        }

        // Client compresses with client params, decompresses with server params
        return new WsPerMessageDeflate(
            clientOptions.CompressionLevel,
            clientOptions.MinMessageSize,
            compressNoContextTakeover: response.ClientNoContextTakeover,
            decompressNoContextTakeover: response.ServerNoContextTakeover);
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