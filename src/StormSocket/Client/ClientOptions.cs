using System.Net;
using StormSocket.Framing;

namespace StormSocket.Client;

/// <summary>
/// Configuration for <see cref="StormTcpClient"/>.
/// </summary>
public sealed class ClientOptions
{
    /// <summary>The server endpoint to connect to. Default: 127.0.0.1:5000.</summary>
    public IPEndPoint EndPoint { get; init; } = new(IPAddress.Loopback, 5000);

    /// <summary>Disables Nagle's algorithm for lower latency. Default: false.</summary>
    public bool NoDelay { get; init; } = false;

    /// <summary>
    /// Enables TCP Keep-Alive to prevent idle connections from being dropped by firewalls/NATs.
    /// Default: true.
    /// </summary>
    public bool KeepAlive { get; init; } = true;

    /// <summary>Maximum bytes waiting to be sent before backpressure kicks in. Default: 1 MB.</summary>
    public long MaxPendingSendBytes { get; init; } = 1024 * 1024;

    /// <summary>Maximum bytes received but not yet processed before pausing reads. Default: 1 MB.</summary>
    public long MaxPendingReceiveBytes { get; init; } = 1024 * 1024;

    /// <summary>Set to enable SSL/TLS encryption. Null = plain TCP.</summary>
    public ClientSslOptions? Ssl { get; init; }

    /// <summary>Message framing strategy. Null = raw bytes (no framing).</summary>
    public IMessageFramer? Framer { get; init; }

    /// <summary>Enable automatic reconnection on disconnect. Default: false.</summary>
    public bool AutoReconnect { get; init; } = false;

    /// <summary>Delay between reconnection attempts. Default: 2 seconds.</summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Maximum reconnection attempts. 0 = unlimited. Default: 0.</summary>
    public int MaxReconnectAttempts { get; init; } = 0;

    /// <summary>Connection timeout. Default: 10 seconds.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
}