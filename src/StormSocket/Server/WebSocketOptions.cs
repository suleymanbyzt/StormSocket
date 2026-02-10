using StormSocket.Core;

namespace StormSocket.Server;

public sealed class WebSocketOptions
{
    /// <summary>
    /// Maximum allowed frame payload size in bytes. Frames larger than this will throw.
    /// Default: 1 MB.
    /// </summary>
    public int MaxFrameSize { get; init; } = 1024 * 1024;

    /// <summary>
    /// List of allowed origins for CSWSH protection (RFC 6455 10.2).
    /// If empty or null, all origins are allowed (default, for non-browser use cases).
    /// </summary>
    /// <example>
    /// <code>
    /// AllowedOrigins = ["https://myapp.com", "https://staging.myapp.com"]
    /// </code>
    /// </example>
    public IReadOnlyList<string>? AllowedOrigins { get; init; }

    /// <summary>
    /// Maximum time to wait for the client to complete the WebSocket upgrade handshake
    /// after the TCP connection is accepted. Connections that don't upgrade within this
    /// window are closed. Set to <see cref="Timeout.InfiniteTimeSpan"/> to disable.
    /// Default: 5 seconds.
    /// </summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Ping/pong heartbeat and dead connection detection settings.</summary>
    public HeartbeatOptions Heartbeat { get; init; } = new();
}
