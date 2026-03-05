using System.IO.Compression;

namespace StormSocket.WebSocket;

/// <summary>
/// Configuration for WebSocket permessage-deflate compression (RFC 7692).
/// Compression is disabled by default.
/// </summary>
public sealed class WsCompressionOptions
{
    /// <summary>Enable permessage-deflate compression. Default: false.</summary>
    public bool Enabled { get; init; } = false;

    /// <summary>DEFLATE compression level. Default: Fastest.</summary>
    public CompressionLevel CompressionLevel { get; init; } = CompressionLevel.Fastest;

    /// <summary>
    /// Minimum payload size in bytes before compression is applied.
    /// Messages smaller than this are sent uncompressed. Default: 128.
    /// </summary>
    public int MinMessageSize { get; init; } = 128;

    /// <summary>
    /// If true, the server does not reuse the compression context across messages.
    /// Each message is compressed independently. Default: true.
    /// </summary>
    public bool ServerNoContextTakeover { get; init; } = true;

    /// <summary>
    /// If true, the client does not reuse the compression context across messages.
    /// Each message is compressed independently. Default: true.
    /// </summary>
    public bool ClientNoContextTakeover { get; init; } = true;

    /// <summary>
    /// Maximum LZ77 sliding window size for the server compressor (9-15). Default: 15.
    /// </summary>
    public int ServerMaxWindowBits { get; init; } = 15;

    /// <summary>
    /// Maximum LZ77 sliding window size for the client compressor (9-15). Default: 15.
    /// </summary>
    public int ClientMaxWindowBits { get; init; } = 15;
}