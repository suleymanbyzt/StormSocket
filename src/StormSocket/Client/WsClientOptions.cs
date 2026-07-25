using Microsoft.Extensions.Logging;
using StormSocket.Core;
using StormSocket.WebSocket;

namespace StormSocket.Client;

/// <summary>
/// Configuration for <see cref="StormWebSocketClient"/>.
/// </summary>
public sealed class WsClientOptions
{
    /// <summary>The WebSocket URI to connect to (ws:// or wss://).</summary>
    public Uri Uri { get; init; } = new("ws://localhost:8080");

    /// <summary>
    /// Budget for the whole connect sequence — DNS, the TCP connect, the TLS handshake and waiting
    /// for the server's <c>101 Switching Protocols</c> response. Default: 10 seconds.
    /// </summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a graceful disconnect waits for the server's Close frame before dropping the
    /// transport (RFC 6455 Section 7.1.4). Also bounds writing the Close frame itself, so an
    /// unresponsive peer cannot stall teardown. Zero closes without waiting. Default: 5 seconds.
    /// </summary>
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(5);

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

    /// <summary>Subprotocols to request during the WebSocket upgrade handshake (Sec-WebSocket-Protocol). Server selects one.</summary>
    public IReadOnlyList<string>? Subprotocols { get; init; }

    /// <summary>SSL options for wss:// connections. Inferred from scheme if null.</summary>
    public ClientSslOptions? Ssl { get; init; }

    /// <summary>Low-level TCP socket tuning (NoDelay, KeepAlive, backpressure limits).</summary>
    public SocketTuningOptions Socket { get; init; } = new();

    /// <summary>Ping/pong heartbeat and dead connection detection settings.</summary>
    public HeartbeatOptions Heartbeat { get; init; } = new();

    /// <summary>Auto-reconnect settings.</summary>
    public ReconnectOptions Reconnect { get; init; } = new();

    /// <summary>Permessage-deflate compression settings (RFC 7692). Disabled by default.</summary>
    public WsCompressionOptions Compression { get; init; } = new();

    /// <summary>Optional logger factory for structured logging. Null = no logging (zero overhead).</summary>
    public ILoggerFactory? LoggerFactory { get; init; }
}
