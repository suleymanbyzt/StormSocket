namespace StormSocket.Core;

/// <summary>
/// WebSocket ping/pong heartbeat settings shared by server and client.
/// </summary>
public sealed class HeartbeatOptions
{
    /// <summary>
    /// Interval between Ping frames. Set to <see cref="TimeSpan.Zero"/> to disable.
    /// Default: 30 seconds.
    /// </summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum number of consecutive missed Pong responses before the connection is considered dead.
    /// Default: 3.
    /// </summary>
    public int MaxMissedPongs { get; init; } = 3;

    /// <summary>
    /// When true, automatically replies to incoming Ping frames with a Pong.
    /// Default: true.
    /// </summary>
    public bool AutoPong { get; init; } = true;
}