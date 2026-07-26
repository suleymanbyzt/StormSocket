using System.IO.Compression;

namespace StormSocket.WebSocket;

/// <summary>
/// Configuration for WebSocket permessage-deflate compression (RFC 7692).
/// Compression is disabled by default.
/// </summary>
public sealed class WsCompressionOptions
{
    /// <summary>Enable permessage-deflate compression. Default: false.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>DEFLATE compression level. Default: Fastest.</summary>
    public CompressionLevel CompressionLevel { get; set; } = CompressionLevel.Fastest;

    /// <summary>
    /// Minimum payload size in bytes before compression is applied.
    /// Messages smaller than this are sent uncompressed. Default: 128.
    /// </summary>
    public int MinMessageSize { get; set; } = 128;

    /// <summary>
    /// If true, the server does not reuse the compression context across messages.
    /// Each message is compressed independently. Default: true.
    /// </summary>
    public bool ServerNoContextTakeover { get; set; } = true;

    /// <summary>
    /// If true, the client does not reuse the compression context across messages.
    /// Each message is compressed independently. Default: true.
    /// </summary>
    public bool ClientNoContextTakeover { get; set; } = true;

    /// <summary>
    /// Maximum LZ77 sliding window size for the server compressor (8-15). Default: 15.
    /// </summary>
    /// <remarks>
    /// <see cref="DeflateStream"/> exposes no window-size control, so this library always compresses
    /// with the full 15-bit window. A peer offer that requires a smaller server window is therefore
    /// declined rather than accepted and ignored (RFC 7692 Section 7.1.2.2).
    /// </remarks>
    public int ServerMaxWindowBits { get; set; } = 15;

    /// <summary>
    /// Upper bound confirmed back to a client that offers <c>client_max_window_bits</c> (8-15). Default: 15.
    /// </summary>
    /// <remarks>
    /// Only used to answer an offer the client actually made; it is never sent unsolicited
    /// (RFC 7692 Section 7.1.2.1). Decompression always uses the full window, so any value is safe to accept.
    /// </remarks>
    public int ClientMaxWindowBits { get; set; } = 15;
}