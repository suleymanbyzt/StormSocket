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
    /// Maximum bytes waiting to be sent before backpressure kicks in.
    /// Default: 1 MB. Set to 0 for unlimited (not recommended for production).
    /// </summary>
    public long MaxPendingSendBytes { get; init; } = 1024 * 1024;

    /// <summary>
    /// Maximum bytes received but not yet processed before pausing reads.
    /// Default: 1 MB. Set to 0 for unlimited (not recommended for production).
    /// </summary>
    public long MaxPendingReceiveBytes { get; init; } = 1024 * 1024;
}