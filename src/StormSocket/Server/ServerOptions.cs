using System.Net;
using StormSocket.Core;
using StormSocket.Framing;

namespace StormSocket.Server;

/// <summary>
/// Configuration for <see cref="StormTcpServer"/> and <see cref="StormWebSocketServer"/>.
/// </summary>
public sealed class ServerOptions
{
    /// <summary>IP and port to listen on. Default: 0.0.0.0:5000.</summary>
    public IPEndPoint EndPoint { get; init; } = new(IPAddress.Any, 5000);

    /// <summary>Maximum pending connection queue length. Default: 128.</summary>
    public int Backlog { get; init; } = 128;

    /// <summary>Disables Nagle's algorithm for lower latency. Default: false.</summary>
    public bool NoDelay { get; init; } = false;

    /// <summary>
    /// Enables TCP Keep-Alive on accepted connections.
    /// Prevents idle connections from being silently dropped by firewalls, NATs, and load balancers.
    /// Default: true.
    /// </summary>
    public bool KeepAlive { get; init; } = true;

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

    /// <summary>
    /// Maximum bytes waiting to be sent before backpressure kicks in.
    /// When this limit is reached, send operations will await until the
    /// socket drains the pending data. Prevents memory exhaustion from slow consumers.
    /// Default: 1 MB. Set to 0 for unlimited (not recommended).
    /// </summary>
    public long MaxPendingSendBytes { get; init; } = 1024 * 1024;

    /// <summary>
    /// Maximum bytes received but not yet processed before pausing socket reads.
    /// Prevents memory exhaustion when message processing is slower than the network.
    /// Default: 1 MB. Set to 0 for unlimited (not recommended).
    /// </summary>
    public long MaxPendingReceiveBytes { get; init; } = 1024 * 1024;

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
    /// Determines behavior when a session's send buffer is full during broadcast.
    /// Wait = block (default), Drop = skip slow sessions, Disconnect = close slow sessions.
    /// </summary>
    public SlowConsumerPolicy SlowConsumerPolicy { get; init; } = SlowConsumerPolicy.Wait;

    /// <summary>
    /// Message framing strategy for TCP servers. Null = raw bytes (no framing).
    /// Use <see cref="LengthPrefixFramer"/>, <see cref="DelimiterFramer"/>, or implement <see cref="IMessageFramer"/>.
    /// </summary>
    public IMessageFramer? Framer { get; init; }
}