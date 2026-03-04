# Configuration Reference

## ServerOptions

| Property | Type | Default | Description |
|---|---|---|---|
| `EndPoint` | `IPEndPoint` | `0.0.0.0:5000` | IP and port to listen on |
| `Backlog` | `int` | `128` | Maximum pending connection queue |
| `DualMode` | `bool` | `false` | Accept both IPv4 and IPv6 on a single port |
| `ReceiveBufferSize` | `int` | `65536` | OS socket receive buffer (bytes) |
| `SendBufferSize` | `int` | `65536` | OS socket send buffer (bytes) |
| `MaxConnections` | `int` | `0` | Max concurrent connections (0 = unlimited). Excess are rejected at TCP level |
| `SlowConsumerPolicy` | `SlowConsumerPolicy` | `Wait` | Behavior when a session is backpressured: `Wait`, `Drop`, or `Disconnect` |
| `Ssl` | `SslOptions?` | `null` | SSL/TLS configuration (null = plain TCP) |
| `WebSocket` | `WebSocketOptions?` | `null` | WebSocket settings (only for StormWebSocketServer) |
| `Framer` | `IMessageFramer?` | `null` | Message framing strategy (null = raw bytes) |
| `Socket` | `SocketTuningOptions` | `new()` | Low-level TCP socket tuning (NoDelay, KeepAlive, backpressure limits) |
| `LoggerFactory` | `ILoggerFactory?` | `null` | Logger factory for structured logging. Null = no logging (zero overhead) |

## WebSocketOptions

| Property | Type | Default | Description |
|---|---|---|---|
| `MaxFrameSize` | `int` | `1048576` | Maximum frame payload (bytes). Oversized frames trigger close with `MessageTooBig` |
| `MaxMessageSize` | `int` | `4194304` | Maximum reassembled message size across all fragments (bytes). Exceeded = close with `MessageTooBig` |
| `AllowedOrigins` | `IReadOnlyList<string>?` | `null` | Allowed origins for CSWSH protection (RFC 6455 #10.2). `null` = allow all |
| `HandshakeTimeout` | `TimeSpan` | `5s` | Max time for client to complete WebSocket upgrade after TCP connect. Prevents DoS via idle connections |
| `Heartbeat` | `HeartbeatOptions` | `new()` | Ping/pong heartbeat and dead connection detection settings |

## ClientOptions

| Property | Type | Default | Description |
|---|---|---|---|
| `EndPoint` | `IPEndPoint` | `127.0.0.1:5000` | Server endpoint to connect to |
| `Ssl` | `ClientSslOptions?` | `null` | SSL/TLS configuration |
| `Framer` | `IMessageFramer?` | `null` | Message framing strategy |
| `ConnectTimeout` | `TimeSpan` | `10s` | Connection timeout |
| `Socket` | `SocketTuningOptions` | `new()` | Low-level TCP socket tuning (NoDelay, KeepAlive, backpressure limits) |
| `Reconnect` | `ReconnectOptions` | `new()` | Auto-reconnect settings |
| `LoggerFactory` | `ILoggerFactory?` | `null` | Logger factory for structured logging. Null = no logging (zero overhead) |

## WsClientOptions

| Property | Type | Default | Description |
|---|---|---|---|
| `Uri` | `Uri` | `ws://localhost:8080` | WebSocket URI (`ws://` or `wss://`) |
| `ConnectTimeout` | `TimeSpan` | `10s` | Connection timeout |
| `MaxFrameSize` | `int` | `1048576` | Maximum frame payload (bytes) |
| `MaxMessageSize` | `int` | `4194304` | Maximum reassembled message size across all fragments (bytes) |
| `Headers` | `Dictionary<string, string>?` | `null` | Extra HTTP headers for upgrade |
| `Ssl` | `ClientSslOptions?` | `null` | SSL/TLS configuration |
| `Socket` | `SocketTuningOptions` | `new()` | Low-level TCP socket tuning (NoDelay, KeepAlive, backpressure limits) |
| `Heartbeat` | `HeartbeatOptions` | `new()` | Ping/pong heartbeat and dead connection detection settings |
| `Reconnect` | `ReconnectOptions` | `new()` | Auto-reconnect settings |
| `LoggerFactory` | `ILoggerFactory?` | `null` | Logger factory for structured logging. Null = no logging (zero overhead) |

## SocketTuningOptions

Shared by `ServerOptions`, `ClientOptions`, and `WsClientOptions`.

| Property | Type | Default | Description |
|---|---|---|---|
| `NoDelay` | `bool` | `false` | Disable Nagle's algorithm for lower latency |
| `KeepAlive` | `bool` | `true` | Enable TCP Keep-Alive to prevent idle connections from being dropped by firewalls/NATs |
| `KeepAliveIdleTime` | `TimeSpan?` | `null` | Idle time before first keep-alive probe (null = OS default, typically 2 hours) |
| `KeepAliveProbeInterval` | `TimeSpan?` | `null` | Interval between keep-alive probes (null = OS default, typically 75 seconds) |
| `KeepAliveProbeCount` | `int?` | `null` | Failed probes before connection is closed (null = OS default, typically 8-10) |
| `MaxPendingSendBytes` | `long` | `1048576` | Max bytes buffered before send backpressure (0 = unlimited) |
| `MaxPendingReceiveBytes` | `long` | `1048576` | Max bytes buffered before receive backpressure (0 = unlimited) |

## HeartbeatOptions

Shared by `WebSocketOptions` (server) and `WsClientOptions` (client).

| Property | Type | Default | Description |
|---|---|---|---|
| `PingInterval` | `TimeSpan` | `30s` | Interval between Ping frames (`TimeSpan.Zero` = disabled) |
| `MaxMissedPongs` | `int` | `3` | Consecutive missed Pongs before closing |
| `AutoPong` | `bool` | `true` | Automatically reply to incoming Ping frames with a Pong |

## ReconnectOptions

Shared by `ClientOptions` and `WsClientOptions`.

| Property | Type | Default | Description |
|---|---|---|---|
| `Enabled` | `bool` | `false` | Enable automatic reconnection on disconnect |
| `Delay` | `TimeSpan` | `2s` | Delay between reconnect attempts |
| `MaxAttempts` | `int` | `0` | Max reconnection attempts (0 = unlimited) |

## SslOptions

| Property | Type | Default | Description |
|---|---|---|---|
| `Certificate` | `X509Certificate2` | *(required)* | Server certificate |
| `Protocols` | `SslProtocols` | `Tls12 \| Tls13` | Allowed TLS protocol versions |
| `ClientCertificateRequired` | `bool` | `false` | Require client certificate |
