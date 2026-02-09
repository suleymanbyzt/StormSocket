namespace StormSocket.Server;

public sealed class WebSocketOptions
{
    /// <summary>
    /// Interval between Ping frames sent to clients. Set to <see cref="TimeSpan.Zero"/> to disable.
    /// Default: 30 seconds.
    /// </summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum number of consecutive heartbeat intervals during which
    /// a Pong response can be missed.
    /// 
    /// If no Pong is received for <see cref="PingInterval"/> multiplied by
    /// <see cref="MaxMissedPongs"/>, the connection is considered dead and
    /// automatically closed.
    /// 
    /// Default: 3.
    /// </summary>
    public int MaxMissedPongs { get; init; } = 3;
    
    /// <summary>
    /// Maximum allowed frame payload size in bytes. Frames larger than this will throw.
    /// Default: 1 MB.
    /// </summary>
    public int MaxFrameSize { get; init; } = 1024 * 1024;

    /// <summary>
    /// When true, the server automatically replies to incoming Ping frames with a Pong.
    /// Default: true.
    /// </summary>
    public bool AutoPong { get; init; } = true;

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
}