namespace StormSocket.Core;

/// <summary>
/// Low-level TCP socket tuning shared by servers and clients.
/// </summary>
public sealed class SocketTuningOptions
{
    /// <summary>Disables Nagle's algorithm for lower latency. Default: false.</summary>
    public bool NoDelay { get; init; } = false;

    /// <summary>
    /// Enables TCP Keep-Alive to prevent idle connections from being dropped by firewalls/NATs.
    /// Default: true.
    /// </summary>
    public bool KeepAlive { get; init; } = true;

    /// <summary>
    /// Idle time before the first keep-alive probe is sent.
    /// Only applied when <see cref="KeepAlive"/> is true. Null = OS default (typically 2 hours).
    /// </summary>
    public TimeSpan? KeepAliveIdleTime { get; init; }

    /// <summary>
    /// Interval between consecutive keep-alive probes.
    /// Only applied when <see cref="KeepAlive"/> is true. Null = OS default (typically 75 seconds).
    /// </summary>
    public TimeSpan? KeepAliveProbeInterval { get; init; }

    /// <summary>
    /// Number of failed keep-alive probes before the connection is considered dead and closed by the OS.
    /// Only applied when <see cref="KeepAlive"/> is true. Null = OS default (typically 8-10).
    /// </summary>
    public int? KeepAliveProbeCount { get; init; }

    /// <summary>
    /// Maximum bytes waiting to be sent before backpressure kicks in.
    /// Default: 1 MB. Set to 0 for unlimited (not recommended for production).
    /// </summary>
    public long MaxPendingSendBytes { get; init; } = 1024 * 1024;

    /// <summary>
    /// Maximum bytes received but not yet processed before pausing reads.
    /// Default: 1 MB. Set to 0 for unlimited (not recommended for production).
    /// </summary>
    public long MaxPendingReceiveBytes { get; init; } = 1024 * 1024;

    /// <summary>
    /// Applies keep-alive settings to the given socket.
    /// </summary>
    internal void ApplyKeepAlive(System.Net.Sockets.Socket socket)
    {
        if (!KeepAlive)
        {
            return;
        }

        socket.SetSocketOption(
            System.Net.Sockets.SocketOptionLevel.Socket,
            System.Net.Sockets.SocketOptionName.KeepAlive,
            true);

        if (KeepAliveIdleTime is not null)
        {
            socket.SetSocketOption(
                System.Net.Sockets.SocketOptionLevel.Tcp,
                System.Net.Sockets.SocketOptionName.TcpKeepAliveTime,
                (int)KeepAliveIdleTime.Value.TotalSeconds);
        }

        if (KeepAliveProbeInterval is not null)
        {
            socket.SetSocketOption(
                System.Net.Sockets.SocketOptionLevel.Tcp,
                System.Net.Sockets.SocketOptionName.TcpKeepAliveInterval,
                (int)KeepAliveProbeInterval.Value.TotalSeconds);
        }

        if (KeepAliveProbeCount is not null)
        {
            socket.SetSocketOption(
                System.Net.Sockets.SocketOptionLevel.Tcp,
                System.Net.Sockets.SocketOptionName.TcpKeepAliveRetryCount,
                KeepAliveProbeCount.Value);
        }
    }
}