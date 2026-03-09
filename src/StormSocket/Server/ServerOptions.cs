using System.Net;
using Microsoft.Extensions.Logging;
using StormSocket.Core;
using StormSocket.Framing;

namespace StormSocket.Server;

/// <summary>
/// Configuration for <see cref="StormTcpServer"/> and <see cref="StormWebSocketServer"/>.
/// </summary>
public sealed class ServerOptions
{
    /// <summary>
    /// Endpoint to listen on. Accepts <see cref="IPEndPoint"/> for TCP/IP or
    /// <see cref="System.Net.Sockets.UnixDomainSocketEndPoint"/> for Unix domain sockets.
    /// Default: 0.0.0.0:5000.
    /// </summary>
    public EndPoint EndPoint { get; init; } = new IPEndPoint(IPAddress.Any, 5000);

    /// <summary>Maximum pending connection queue length. Default: 128.</summary>
    public int Backlog { get; init; } = 128;

    /// <summary>
    /// Enables dual-mode socket that accepts both IPv4 and IPv6 connections on a single port.
    /// When enabled, the server listens on IPv6Any and maps IPv4 clients to IPv6 addresses (e.g. ::ffff:192.168.1.1).
    /// Default: false.
    /// </summary>
    public bool DualMode { get; init; } = false;

    /// <summary>Socket receive buffer size in bytes. Default: 64 KB.</summary>
    public int ReceiveBufferSize { get; init; } = 65536;

    /// <summary>Socket send buffer size in bytes. Default: 64 KB.</summary>
    public int SendBufferSize { get; init; } = 65536;

    /// <summary>Set to enable SSL/TLS encryption on all connections. Null = plain TCP.</summary>
    public SslOptions? Ssl { get; init; }

    /// <summary>WebSocket-specific settings. Only used by <see cref="StormWebSocketServer"/>.</summary>
    public WebSocketOptions? WebSocket { get; init; }

    /// <summary>
    /// Maximum number of concurrent connections. 0 = unlimited. Default: 0.
    /// When the limit is reached, new connections are immediately closed.
    /// </summary>
    public int MaxConnections { get; init; } = 0;

    /// <summary>
    /// Determines behavior when a session's send buffer reaches <see cref="SocketTuningOptions.MaxPendingSendBytes"/>.
    /// Applies to both broadcast and individual <c>SendAsync</c> calls.
    /// <list type="bullet">
    /// <item><b>Wait</b> (default): Awaits until the socket drains. Safe but a slow client can stall the caller.</item>
    /// <item><b>Drop</b>: Silently discards the message. Best for real-time data (chat, game state, tickers).</item>
    /// <item><b>Disconnect</b>: Calls <c>Abort()</c> to immediately terminate the session. Best for critical feeds where all clients must keep up.</item>
    /// </list>
    /// </summary>
    public SlowConsumerPolicy SlowConsumerPolicy { get; init; } = SlowConsumerPolicy.Wait;

    /// <summary>
    /// Message framing strategy for TCP servers. Null = raw bytes (no framing).
    /// Use <see cref="LengthPrefixFramer"/>, <see cref="DelimiterFramer"/>, or implement <see cref="IMessageFramer"/>.
    /// </summary>
    public IMessageFramer? Framer { get; init; }

    /// <summary>
    /// Maximum time a connection can remain idle (no application-level data received)
    /// before being automatically closed. Set to <see cref="TimeSpan.Zero"/> to disable.
    /// For WebSocket servers, use <see cref="WebSocketOptions.IdleTimeout"/> instead.
    /// Default: disabled.
    /// </summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.Zero;

    /// <summary>Low-level TCP socket tuning (NoDelay, KeepAlive, backpressure limits).</summary>
    public SocketTuningOptions Socket { get; init; } = new();

    /// <summary>
    /// Optional logger factory for structured logging. Null = no logging (zero overhead).
    /// <example>
    /// <code>
    /// var server = new StormTcpServer(new ServerOptions
    /// {
    ///     LoggerFactory = LoggerFactory.Create(b => b.AddConsole()),
    /// });
    /// </code>
    /// </example>
    /// </summary>
    public ILoggerFactory? LoggerFactory { get; init; }
}