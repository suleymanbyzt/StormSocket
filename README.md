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

- **Pooled I/O** — `System.IO.Pipelines` buffer pool, `ArrayPool` for send encoding, and per-connection unmask buffers, so steady-state message traffic allocates almost nothing
- **Backpressure that actually works** — configurable pipe thresholds, OS TCP window propagates upstream
- **Subscribe, don't subclass** — `server.OnDataReceived += handler`, no inheritance chains
- **SSL is a config flag** — same server, add `SslOptions`, done
- **Middleware pipeline** — rate limiting, auth, logging as composable plugins
- **Built-in pub/sub** — named groups for chat rooms, game lobbies, ticker feeds

# Features

- **Event-based API** - no subclassing, just `server.OnDataReceived += handler`
- **TCP Server & Client** with optional message framing (raw, length-prefix, delimiter)
- **WebSocket Server & Client** built to RFC 6455 — and it *validates* the protocol rather than only speaking it: masking enforced in both directions, incremental UTF-8 validation across fragments (1007), close-code and close-reason validation (1002/1007), single close frame with a proper closing handshake, control-frame and frame-length rules, strict handshake validation
- **SSL/TLS** as a simple configuration option on any server or client
- **Auto-reconnect** - clients automatically reconnect on disconnect with configurable delay and max attempts
- **System.IO.Pipelines** — received payloads are handed to handlers without a copy where the protocol allows it, with backpressure that reaches the socket
- **Automatic heartbeat** with configurable ping interval and dead connection detection (missed pong counting)
- **Session management** - track, query, broadcast, and kick connections
- **Groups/Rooms** - named groups for targeted broadcast (chat rooms, game lobbies, etc.)
- **WebSocket authentication** - access path, query params, headers (cookies, tokens) before accepting connections via `OnConnecting` event
- **Rate limiting middleware** - opt-in per-session or per-IP limiting with a sliding window, configurable action (disconnect/drop) and exceeded event; meters control frames and fragments too, so a ping flood costs the sender budget
- **Middleware pipeline** - intercept connect, disconnect, data received, data sending, and errors (works on both server and client)
- **Backpressure & buffer limits** - configurable send/receive pipe limits prevent memory exhaustion
- **[Per-session user data](docs/features.md#per-session-user-data)** - `session.Items` dictionary + strongly-typed `session.Get<T>` / `session.Set<T>` via `SessionKey<T>` — no external dictionary needed
- **Slow consumer detection** - `SlowConsumerPolicy` per session: `Wait` (block), `Drop` (skip), or `Disconnect` (close)
- **Message fragmentation** - automatic reassembly of fragmented WebSocket messages (RFC 6455 Section 5.4) with `MaxMessageSize` limit and send-side fragmentation helpers
- **Connection idle timeout** - automatically close connections that haven't sent any application-level data within a configurable period (ping/pong does NOT reset the timer)
- **Disconnect reason tracking** - `OnDisconnected` provides a `DisconnectReason` enum (`ClosedByClient`, `ClosedByServer`, `Aborted`, `ProtocolError`, `TransportError`, `HeartbeatTimeout`, `HandshakeTimeout`, `SlowConsumer`, `GoingAway`, `RateLimited`, `IdleTimeout`, `MessageTooBig`)
- **Handshake timeout** - configurable timeout for the WebSocket upgrade, plus a separate `TlsHandshakeTimeout` so a peer cannot park mid-TLS
- **Connection limits that count half-open connections** - `MaxConnections` and `MaxConnectionsPerIp` are claimed at accept time, before TLS and the upgrade, so slowloris-style connections cannot walk past them
- **Bounded upgrade parsing** - `MaxRequestHeaderBytes` / `MaxRequestHeaderCount` (16 KB / 100 by default), answered with 431
- **Bounded decompression** - permessage-deflate output is capped at `MaxMessageSize`, so a compression bomb fails the connection with 1009 instead of the process
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

> `msg.Data` points into a buffer the connection reuses for the next frame, so it is valid for the
> duration of the handler. Copy it (`msg.Data.ToArray()`) if it outlives the call — a queue, a field,
> a captured closure. `msg.Text` already returns an independent string.

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
| **System.IO.Pipelines** | Copy-free receive path where the protocol allows it, backpressure down to the socket. |
| **Event-based API** | Subscribe to events, no need to subclass. |
| **SSL as decorator** | Same server, just add `SslOptions`. |
| **Integer session IDs** | `Interlocked.Increment` (fast, sortable) instead of Guid. |
| **Write serialization** | Per-session `SemaphoreSlim` lock prevents frame corruption. |
| **Interface hierarchy** | `ISession` (base) → `IWebSocketSession` (WS-specific with `SendTextAsync`). Clean, flat hierarchy. |

# Benchmarks

Two different things get called "performance", and they are measured separately here because one
cannot be derived from the other: a deeply pipelined run can push millions of messages per second
while any single message waits milliseconds behind the ones queued ahead of it.

Numbers below are from an Apple M-series laptop, .NET 9, server GC, loopback, client and server on
the same machine — treat them as a shape, not a spec sheet, and run them yourself.

### Latency — measured round-trips, pipeline depth 1

Every message is timed individually with `Stopwatch.GetTimestamp`, and the next one is not sent until
the echo comes back.

| Echo round-trip, 32-byte messages | p50 | p90 | p99 | p99.9 |
|---|---|---|---|---|
| TCP, 1 connection | 46 us | 64 us | 81 us | 93 us |
| TCP, 50 connections | 282 us | 344 us | 606 us | 1.6 ms |
| WebSocket, 1 connection | 46 us | 64 us | 81 us | 95 us |
| WebSocket, 50 connections | 276 us | 329 us | 505 us | 982 us |

```bash
dotnet run -c Release --project benchmark/StormSocket.Benchmark.TcpEchoServer
dotnet run -c Release --project benchmark/StormSocket.Benchmark.TcpEchoClient -- -c 50 -s 32 -z 10 --mode latency
```

### Throughput — saturation, deep pipeline

100 connections each keeping a 1000-message send window full (~100k messages in flight). This is a
saturation figure: it says how much the server moves, not how long a message takes.

| Echo, 32-byte messages | Data throughput |
|---|---|
| TCP | 1.03 GiB/s |
| WebSocket | ~76 MiB/s |

```bash
dotnet run -c Release --project benchmark/StormSocket.Benchmark.TcpEchoClient -- -c 100 -m 1000 -s 32 -z 10
```

### Frame decoding

Decode path in isolation, no sockets involved — this is where the WebSocket layer's own cost lives
(best of five runs, per frame):

| Payload | v4.0.1 | v5.0.0 |
|---|---|---|
| 32 B | 51 ns | 58 ns |
| 128 B | 104 ns | 60 ns |
| 1 KB | 586 ns | 109 ns |
| 8 KB | 4.42 us | 486 ns |

Unmasking is vectorized in 5.0, which is what moves the larger payloads. The 32-byte row goes the
other way: that is the protocol validation added in 5.0 (masking, close codes, UTF-8, frame limits),
and it is a trade this project is willing to make. Allocation per message on the server dropped from
156 to 109 bytes over a 25M-message run, and gen0 collections from 46 to 28.

> No comparison against other libraries is published here. A fair one needs the competing harness
> committed next to ours, pinned versions and disclosed hardware; until that exists, numbers you
> cannot reproduce are not worth printing.

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
| [Changelog](CHANGELOG.md) | Release notes, and the breaking changes in 5.0 |

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
