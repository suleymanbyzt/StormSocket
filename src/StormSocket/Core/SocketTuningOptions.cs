namespace StormSocket.Core;

/// <summary>
/// Low-level TCP socket tuning shared by servers and clients.
/// </summary>
public sealed class SocketTuningOptions
{
    /// <summary>Disables Nagle's algorithm for lower latency. Default: false.</summary>
    public bool NoDelay { get; set; } = false;

    /// <summary>
    /// Enables TCP Keep-Alive to prevent idle connections from being dropped by firewalls/NATs.
    /// Default: true.
    /// </summary>
    public bool KeepAlive { get; set; } = true;

    /// <summary>
    /// Idle time before the first keep-alive probe is sent.
    /// Only applied when <see cref="KeepAlive"/> is true. Null = OS default (typically 2 hours).
    /// </summary>
    public TimeSpan? KeepAliveIdleTime { get; set; }

    /// <summary>
    /// Interval between consecutive keep-alive probes.
    /// Only applied when <see cref="KeepAlive"/> is true. Null = OS default (typically 75 seconds).
    /// </summary>
    public TimeSpan? KeepAliveProbeInterval { get; set; }

    /// <summary>
    /// Number of failed keep-alive probes before the connection is considered dead and closed by the OS.
    /// Only applied when <see cref="KeepAlive"/> is true. Null = OS default (typically 8-10).
    /// </summary>
    public int? KeepAliveProbeCount { get; set; }

    /// <summary>
    /// Maximum bytes waiting to be sent before backpressure kicks in.
    /// Default: 1 MB. Set to 0 for unlimited (not recommended for production).
    /// </summary>
    public long MaxPendingSendBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// Maximum bytes received but not yet processed before pausing reads.
    /// Default: 1 MB. Set to 0 for unlimited (not recommended for production).
    /// </summary>
    public long MaxPendingReceiveBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// Checks the backpressure limits and keep-alive intervals. Called by the server and client
    /// options' own <c>Validate</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A limit or interval is outside its allowed range.</exception>
    public void Validate()
    {
        OptionsValidation.RequireNonNegative(MaxPendingSendBytes, nameof(SocketTuningOptions), nameof(MaxPendingSendBytes));
        OptionsValidation.RequireNonNegative(MaxPendingReceiveBytes, nameof(SocketTuningOptions), nameof(MaxPendingReceiveBytes));

        if (KeepAliveIdleTime is TimeSpan idleTime)
        {
            OptionsValidation.RequirePositiveDuration(idleTime, nameof(SocketTuningOptions), nameof(KeepAliveIdleTime), allowInfinite: false);
        }

        if (KeepAliveProbeInterval is TimeSpan probeInterval)
        {
            OptionsValidation.RequirePositiveDuration(probeInterval, nameof(SocketTuningOptions), nameof(KeepAliveProbeInterval), allowInfinite: false);
        }

        if (KeepAliveProbeCount is int probeCount)
        {
            OptionsValidation.RequirePositive(probeCount, nameof(SocketTuningOptions), nameof(KeepAliveProbeCount));
        }
    }

    /// <summary>
    /// Applies keep-alive settings to the given socket.
    /// </summary>
    internal void ApplyKeepAlive(System.Net.Sockets.Socket socket)
    {
        // KeepAlive is not supported on Unix domain sockets
        if (!KeepAlive || socket.AddressFamily == System.Net.Sockets.AddressFamily.Unix)
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