namespace StormSocket.Client;

/// <summary>
/// Configuration for <see cref="StormWebSocketClient"/>.
/// </summary>
public sealed class WsClientOptions
{
    /// <summary>The WebSocket URI to connect to (ws:// or wss://).</summary>
    public Uri Uri { get; init; } = new("ws://localhost:8080");

    /// <summary>Disables Nagle's algorithm. Default: false.</summary>
    public bool NoDelay { get; init; } = false;

    /// <summary>
    /// Enables TCP Keep-Alive to prevent idle connections from being dropped by firewalls/NATs.
    /// Default: true.
    /// </summary>
    public bool KeepAlive { get; init; } = true;

    /// <summary>Maximum bytes waiting to be sent before backpressure. Default: 1 MB.</summary>
    public long MaxPendingSendBytes { get; init; } = 1024 * 1024;

    /// <summary>Maximum bytes received but not yet processed. Default: 1 MB.</summary>
    public long MaxPendingReceiveBytes { get; init; } = 1024 * 1024;

    /// <summary>Maximum allowed frame payload size. Default: 1 MB.</summary>
    public int MaxFrameSize { get; init; } = 1024 * 1024;

    /// <summary>Interval between Ping frames sent to the server. Zero = disabled. Default: 30s.</summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Max missed Pongs before considering connection dead. Default: 3.</summary>
    public int MaxMissedPongs { get; init; } = 3;

    /// <summary>Automatically reply to server Ping with Pong. Default: true.</summary>
    public bool AutoPong { get; init; } = true;

    /// <summary>Enable automatic reconnection. Default: false.</summary>
    public bool AutoReconnect { get; init; } = false;

    /// <summary>Delay between reconnection attempts. Default: 2 seconds.</summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Maximum reconnect attempts. 0 = unlimited. Default: 0.</summary>
    public int MaxReconnectAttempts { get; init; } = 0;

    /// <summary>Connection timeout. Default: 10 seconds.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Additional HTTP headers to send during the WebSocket upgrade request.</summary>
    public Dictionary<string, string>? Headers { get; init; }

    /// <summary>SSL options for wss:// connections. Inferred from scheme if null.</summary>
    public ClientSslOptions? Ssl { get; init; }
}