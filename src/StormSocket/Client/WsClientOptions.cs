using Microsoft.Extensions.Logging;
using StormSocket.Core;

namespace StormSocket.Client;

/// <summary>
/// Configuration for <see cref="StormWebSocketClient"/>.
/// </summary>
public sealed class WsClientOptions
{
    /// <summary>The WebSocket URI to connect to (ws:// or wss://).</summary>
    public Uri Uri { get; init; } = new("ws://localhost:8080");

    /// <summary>Connection timeout. Default: 10 seconds.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Maximum allowed frame payload size. Default: 1 MB.</summary>
    public int MaxFrameSize { get; init; } = 1024 * 1024;

    /// <summary>
    /// Maximum total size of a reassembled WebSocket message across all fragments.
    /// Messages exceeding this limit will trigger a close with status 1009 (MessageTooBig).
    /// Default: 4 MB.
    /// </summary>
    public int MaxMessageSize { get; init; } = 4 * 1024 * 1024;

    /// <summary>Additional HTTP headers to send during the WebSocket upgrade request.</summary>
    public Dictionary<string, string>? Headers { get; init; }

    /// <summary>SSL options for wss:// connections. Inferred from scheme if null.</summary>
    public ClientSslOptions? Ssl { get; init; }

    /// <summary>Low-level TCP socket tuning (NoDelay, KeepAlive, backpressure limits).</summary>
    public SocketTuningOptions Socket { get; init; } = new();

    /// <summary>Ping/pong heartbeat and dead connection detection settings.</summary>
    public HeartbeatOptions Heartbeat { get; init; } = new();

    /// <summary>Auto-reconnect settings.</summary>
    public ReconnectOptions Reconnect { get; init; } = new();

    /// <summary>Optional logger factory for structured logging. Null = no logging (zero overhead).</summary>
    public ILoggerFactory? LoggerFactory { get; init; }
}
