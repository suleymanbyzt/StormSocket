<p align="center">
  <img src="assets/stormsocket-logo.png" alt="StormSocket" width="256" />
</p>

<h1 align="center">StormSocket</h1>

<p align="center">
  <a href="https://github.com/suleymanbyzt/StormSocket/actions"><img src="https://img.shields.io/github/actions/workflow/status/suleymanbyzt/StormSocket/ci.yml?branch=master" alt="Build" /></a>
  <img src="https://img.shields.io/endpoint?url=https://gist.githubusercontent.com/suleymanbyzt/536cdf59b30363682a17ef0927ed7f45/raw/stormsocket-coverage.json" alt="Coverage" />
  <a href="https://www.nuget.org/packages/StormSocket"><img src="https://img.shields.io/nuget/v/StormSocket.svg" alt="NuGet" /></a>
  <a href="https://www.nuget.org/packages/StormSocket"><img src="https://img.shields.io/nuget/dt/StormSocket.svg" alt="NuGet Downloads" /></a>
  <a href="https://github.com/suleymanbyzt/StormSocket/stargazers"><img src="https://img.shields.io/github/stars/suleymanbyzt/StormSocket" alt="Stars" /></a>
  <a href="https://github.com/suleymanbyzt/StormSocket/issues"><img src="https://img.shields.io/github/issues/suleymanbyzt/StormSocket" alt="Issues" /></a>
  <img src="https://img.shields.io/github/last-commit/suleymanbyzt/StormSocket" alt="Last Commit" />
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-green.svg" alt="License" /></a>
  <img src="https://img.shields.io/badge/.NET-6.0%20|%207.0%20|%208.0%20|%209.0%20|%2010.0-blue" alt=".NET" />
</p>

<p align="center">Modern, high-performance, event-based TCP/WebSocket/SSL library for .NET built on <b>System.IO.Pipelines</b>.</p>

<p align="center">
  <a href="https://suleymanbyzt.github.io/StormSocket/docs/getting-started.html"><b>Getting Started</b></a> · <a href="https://suleymanbyzt.github.io/StormSocket/docs/features.html">Features</a> · <a href="https://suleymanbyzt.github.io/StormSocket/docs/configuration.html">Configuration</a> · <a href="https://suleymanbyzt.github.io/StormSocket/docs/examples.html">Examples</a>
</p>

Zero subclassing required. Subscribe to events, configure options, and go. Server and client included.

## Why StormSocket?

- **Pooled I/O** — `System.IO.Pipelines` buffer pool + `ArrayPool` for send encoding, minimal GC pressure
- **Backpressure that actually works** — configurable pipe thresholds, OS TCP window propagates upstream
- **Subscribe, don't subclass** — `server.OnDataReceived += handler`, no inheritance chains
- **SSL is a config flag** — same server, add `SslOptions`, done
- **Middleware pipeline** — rate limiting, auth, logging as composable plugins
- **Built-in pub/sub** — named groups for chat rooms, game lobbies, ticker feeds

# Features

- **Event-based API** - no subclassing, just `server.OnDataReceived += handler`
- **TCP Server & Client** with optional message framing (raw, length-prefix, delimiter)
- **WebSocket Server & Client** with full RFC 6455 compliance (text, binary, ping/pong, close, fragmentation/continuation frames, client-side masking)
- **SSL/TLS** as a simple configuration option on any server or client
- **Auto-reconnect** - clients automatically reconnect on disconnect with configurable delay and max attempts
- **System.IO.Pipelines** for zero-copy I/O with built-in backpressure
- **Automatic heartbeat** with configurable ping interval and dead connection detection (missed pong counting)
- **Session management** - track, query, broadcast, and kick connections
- **Groups/Rooms** - named groups for targeted broadcast (chat rooms, game lobbies, etc.)
- **WebSocket authentication** - access path, query params, headers (cookies, tokens) before accepting connections via `OnConnecting` event
- **Rate limiting middleware** - opt-in per-session or per-IP rate limiting with configurable window, action (disconnect/drop), and exceeded event
- **Middleware pipeline** - intercept connect, disconnect, data received, data sending, and errors (works on both server and client)
- **Backpressure & buffer limits** - configurable send/receive pipe limits prevent memory exhaustion
- **[Per-session user data](docs/features.md#per-session-user-data)** - `session.Items` dictionary + strongly-typed `session.Get<T>` / `session.Set<T>` via `SessionKey<T>` — no external dictionary needed
- **Slow consumer detection** - `SlowConsumerPolicy` per session: `Wait` (block), `Drop` (skip), or `Disconnect` (close)
- **Message fragmentation** - automatic reassembly of fragmented WebSocket messages (RFC 6455 Section 5.4) with `MaxMessageSize` limit and send-side fragmentation helpers
- **Connection idle timeout** - automatically close connections that haven't sent any application-level data within a configurable period (ping/pong does NOT reset the timer)
- **Disconnect reason tracking** - `OnDisconnected` provides a `DisconnectReason` enum (`ClosedByClient`, `ClosedByServer`, `Aborted`, `ProtocolError`, `TransportError`, `HeartbeatTimeout`, `HandshakeTimeout`, `SlowConsumer`, `GoingAway`, `RateLimited`, `IdleTimeout`, `MessageTooBig`)
- **Handshake timeout** - configurable timeout for WebSocket upgrade (DoS protection)
- **TCP Keep-Alive** - fine-tuning options (idle time, probe interval, probe count)
- **Unix domain socket** transport — same API, just pass `UnixDomainSocketEndPoint` for fast local IPC (container-to-container, sidecar proxy)
- **Multi-target**: net6.0, net7.0, net8.0, net9.0, net10.0
- **Server metrics** - `server.Metrics` exposes active connections, total connections, messages sent/received, bytes, errors — built on `System.Diagnostics.Metrics` for native OpenTelemetry/Prometheus/`dotnet-counters` integration
- **Structured logging** via `ILoggerFactory` — zero overhead when disabled, structured output when enabled
- **Zero dependencies** beyond `System.IO.Pipelines` and `Microsoft.Extensions.Logging.Abstractions`

# Quick Start

```bash
dotnet add package StormSocket
```

### TCP Server

```csharp
var server = new StormTcpServer(new ServerOptions
{
    EndPoint = new IPEndPoint(IPAddress.Any, 5000),
});

server.OnDataReceived += async (session, data) =>
{
    await session.SendAsync(data); // echo
};

await server.StartAsync();
```

### WebSocket Server

```csharp
var ws = new StormWebSocketServer(new ServerOptions
{
    EndPoint = new IPEndPoint(IPAddress.Any, 8080),
    WebSocket = new WebSocketOptions
    {
        Heartbeat = new() { PingInterval = TimeSpan.FromSeconds(15), MaxMissedPongs = 3 },
        Compression = new() { Enabled = true },
    },
});

ws.OnConnected += async session =>
{
    session.Set(UserId, "abc");       // type-safe session data
    ws.Groups.Add("lobby", session);  // join a room
};

ws.OnMessageReceived += async (session, msg) =>
{
    await ws.BroadcastTextAsync(msg.Text, excludeId: session.Id);
};

await ws.StartAsync();
```

### WebSocket Client

```csharp
var client = new StormWebSocketClient(new WsClientOptions
{
    Uri = new Uri("ws://localhost:8080"),
    Reconnect = new() { Enabled = true, Delay = TimeSpan.FromSeconds(2) },
    Subprotocols = ["graphql-ws"],
});

client.OnMessageReceived += async msg => Console.WriteLine(msg.Text);
await client.ConnectAsync();
await client.SendTextAsync("Hello!");
```

### SSL — one line

```csharp
var server = new StormWebSocketServer(new ServerOptions
{
    EndPoint = new IPEndPoint(IPAddress.Any, 443),
    Ssl = new() { Certificate = X509CertificateLoader.LoadPkcs12FromFile("cert.pfx", "pass") },
});
```

### Authentication

```csharp
ws.OnConnecting += async context =>
{
    string? token = context.Headers.GetValueOrDefault("Authorization");
    if (!IsValid(token))
        context.Reject(401, "Invalid token");
    else
        context.Accept();
};
```

### Session Data — string keys or type-safe

```csharp
// String keys
session.Items["role"] = "admin";

// Strongly-typed (no casts, compile-time safe)
static readonly SessionKey<string> UserId = new("userId");
session.Set(UserId, "abc123");
string id = session.Get(UserId);
```

### Groups, Broadcast, Rate Limiting

```csharp
ws.Groups.Add("room:vip", session);
await ws.Groups.BroadcastAsync("room:vip", data, excludeId: session.Id);

server.UseMiddleware(new RateLimitMiddleware(new RateLimitOptions
{
    Window = TimeSpan.FromSeconds(10), MaxMessages = 100,
}));
```

### Slow Consumer Policy

```csharp
var server = new StormWebSocketServer(new ServerOptions
{
    SlowConsumerPolicy = SlowConsumerPolicy.Drop, // Wait | Drop | Disconnect
    MaxConnections = 50_000,
});
```

### Unix Domain Socket

```csharp
// Server — fast local IPC, skips the TCP/IP stack entirely
var server = new StormTcpServer(new ServerOptions
{
    EndPoint = new UnixDomainSocketEndPoint("/tmp/myapp.sock"),
});

// Client
var client = new StormTcpClient(new ClientOptions
{
    EndPoint = new UnixDomainSocketEndPoint("/tmp/myapp.sock"),
});
```

### Idle Timeout

```csharp
var server = new StormWebSocketServer(new ServerOptions
{
    WebSocket = new WebSocketOptions
    {
        IdleTimeout = TimeSpan.FromMinutes(5), // close if no data for 5 min
    },
});
// ping/pong does NOT reset the timer — only real messages count
```

### Server Metrics

```csharp
// Direct property access
var m = server.Metrics;
Console.WriteLine($"Active: {m.ActiveConnections}, Total: {m.TotalConnections}");
Console.WriteLine($"Msgs in: {m.MessagesReceived}, Msgs out: {m.MessagesSent}");
Console.WriteLine($"Bytes in: {m.BytesReceivedTotal}, Bytes out: {m.BytesSentTotal}");
Console.WriteLine($"Errors: {m.ErrorCount}");
```

Built on `System.Diagnostics.Metrics` — works with OpenTelemetry, Prometheus, and `dotnet-counters` out of the box:

```csharp
// OpenTelemetry integration (just add an exporter)
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("StormSocket").AddPrometheusExporter());

// Or monitor from CLI — no code needed:
// $ dotnet-counters monitor --counters StormSocket
```

| Meter: `StormSocket` | Type | Description |
|---|---|---|
| `stormsocket.connections.total` | Counter | Total connections accepted |
| `stormsocket.connections.active` | UpDownCounter | Currently active connections |
| `stormsocket.messages.sent` | Counter | Total messages sent |
| `stormsocket.messages.received` | Counter | Total messages received |
| `stormsocket.bytes.sent` | Counter | Total bytes sent |
| `stormsocket.bytes.received` | Counter | Total bytes received |
| `stormsocket.errors` | Counter | Total errors |
| `stormsocket.connection.duration` | Histogram | Connection duration (ms) |
| `stormsocket.handshake.duration` | Histogram | Handshake duration (ms) |

### Disconnect Reasons

```csharp
ws.OnDisconnected += async (session, reason) =>
{
    // ClosedByClient | ClosedByServer | HeartbeatTimeout | SlowConsumer
    // ProtocolError | TransportError | RateLimited | GoingAway | IdleTimeout | ...
    Console.WriteLine($"#{session.Id}: {reason}");
};
```

> Full details: [Features Guide](docs/features.md) | [Examples](docs/examples.md) | [Configuration](docs/configuration.md)

# Architecture

| Principle | How |
|---|---|
| **Composition over inheritance** | Flat structure, no deep inheritance chains. |
| **System.IO.Pipelines** | Zero-copy I/O with kernel-level backpressure. |
| **Event-based API** | Subscribe to events, no need to subclass. |
| **SSL as decorator** | Same server, just add `SslOptions`. |
| **Integer session IDs** | `Interlocked.Increment` (fast, sortable) instead of Guid. |
| **Write serialization** | Per-session `SemaphoreSlim` lock prevents frame corruption. |
| **Interface hierarchy** | `ISession` (base) → `IWebSocketSession` (WS-specific with `SendTextAsync`). Clean, flat hierarchy. |

# Benchmarks

Echo round-trip: 100 concurrent clients, 32-byte messages, 10-second sustained load.

### TCP Echo

| Metric | StormSocket | NetCoreServer |
|---|---|---|
| **Throughput** | 342 MiB/s | 73 MiB/s |
| **Messages/sec** | 11,205,120 | 2,386,789 |
| **Latency** | 89 ns | 418 ns |

### WebSocket Echo

| Metric | StormSocket | NetCoreServer |
|---|---|---|
| **Throughput** | 66 MiB/s | 40 MiB/s |
| **Messages/sec** | 2,163,373 | 1,309,842 |
| **Latency** | 462 ns | 763 ns |

> Results will vary by hardware. Benchmark projects under `benchmark/` — run them yourself.

```bash
dotnet run -c Release --project benchmark/StormSocket.Benchmark.TcpEchoServer
dotnet run -c Release --project benchmark/StormSocket.Benchmark.TcpEchoClient -- -c 100 -m 1000 -s 32 -z 10
```

# Documentation

| Guide | Description |
|---|---|
| [Getting Started](docs/getting-started.md) | Installation, first TCP server, first WebSocket server |
| [Examples](docs/examples.md) | TCP echo, WebSocket chat, auth, SSL, clients, admin console |
| [Features Guide](docs/features.md) | Sessions, groups, framing, heartbeat, slow consumer, rate limiting, fragmentation, disconnect reasons |
| [Middleware](docs/middleware.md) | Pipeline, custom middleware, built-in rate limiting |
| [Configuration](docs/configuration.md) | All options tables (ServerOptions, WebSocketOptions, ClientOptions, etc.) |
| [API Reference](docs/api-reference.md) | ISession, IWebSocketSession, clients, middleware, framers |
| [Architecture](docs/architecture.md) | Connection lifecycle, write serialization, backpressure internals |

# Building

```bash
git clone https://github.com/suleymanbyzt/StormSocket.git
cd StormSocket
dotnet build
dotnet test
```

# Samples

| Sample | Port | Description |
|---|---|---|
| `StormSocket.Samples.TcpEcho` | 5000 | TCP echo server, test with `telnet` |
| `StormSocket.Samples.WsChat` | 8080 | WebSocket broadcast chat |
| `StormSocket.Samples.SslEcho` | 5001 | SSL/TLS echo with self-signed cert |
| `StormSocket.Samples.WsServer` | 8080 | Full-featured WS server with admin console, rooms, heartbeat |

```bash
dotnet run --project samples/StormSocket.Samples.TcpEcho
dotnet run --project samples/StormSocket.Samples.WsChat
dotnet run --project samples/StormSocket.Samples.SslEcho
dotnet run --project samples/StormSocket.Samples.WsServer
```

# License

MIT License - see [LICENSE](LICENSE) for details.
