<p align="center">
  <img src="assets/stormsocket.png" alt="StormSocket" width="256" />
</p>

<h1 align="center">StormSocket</h1>

<p align="center">
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-green.svg" alt="License" /></a>
  <a href="https://www.nuget.org/packages/StormSocket"><img src="https://img.shields.io/nuget/v/StormSocket.svg" alt="NuGet" /></a>
  <img src="https://img.shields.io/badge/.NET-6.0%20|%207.0%20|%208.0%20|%209.0%20|%2010.0-blue" alt=".NET" />
</p>

<p align="center">Modern, high-performance, event-based TCP/WebSocket/SSL library for .NET built on <b>System.IO.Pipelines</b>.</p>

<p align="center">
  <a href="https://suleymanbyzt.github.io/StormSocket/docs/getting-started.html"><b>Getting Started</b></a> · <a href="https://suleymanbyzt.github.io/StormSocket/docs/features.html">Features</a> · <a href="https://suleymanbyzt.github.io/StormSocket/docs/configuration.html">Configuration</a> · <a href="https://suleymanbyzt.github.io/StormSocket/docs/examples.html">Examples</a>
</p>

Zero subclassing required. Subscribe to events, configure options, and go. Server and client included.

## Why StormSocket?

- **Pooled, allocation-free I/O** — `System.IO.Pipelines` manages buffer pools internally, no `new byte[]` per read
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
- **Slow consumer detection** - `SlowConsumerPolicy` per session: `Wait` (block), `Drop` (skip), or `Disconnect` (close)
- **Message fragmentation** - automatic reassembly of fragmented WebSocket messages (RFC 6455 Section 5.4) with `MaxMessageSize` limit and send-side fragmentation helpers
- **Disconnect reason tracking** - `OnDisconnected` provides a `DisconnectReason` enum (`ClosedByClient`, `ClosedByServer`, `Aborted`, `ProtocolError`, `TransportError`, `HeartbeatTimeout`, `HandshakeTimeout`, `SlowConsumer`, `GoingAway`, `RateLimited`)
- **Handshake timeout** - configurable timeout for WebSocket upgrade (DoS protection)
- **TCP Keep-Alive** - fine-tuning options (idle time, probe interval, probe count)
- **Multi-target**: net6.0, net7.0, net8.0, net9.0, net10.0
- **Structured logging** via `ILoggerFactory` — zero overhead when disabled, structured output when enabled
- **Zero dependencies** beyond `System.IO.Pipelines` and `Microsoft.Extensions.Logging.Abstractions`

# Quick Start

```bash
dotnet add package StormSocket
```

```csharp
using System.Net;
using StormSocket.Server;

var server = new StormTcpServer(new ServerOptions
{
    EndPoint = new IPEndPoint(IPAddress.Any, 5000),
});

server.OnDataReceived += async (session, data) =>
{
    await session.SendAsync(data); // echo
};

await server.StartAsync();
Console.ReadLine();
await server.DisposeAsync();
```

# Architecture

| Principle | How |
|---|---|
| **Composition over inheritance** | Flat structure, no deep inheritance chains. |
| **System.IO.Pipelines** | Zero-copy I/O with kernel-level backpressure. |
| **Event-based API** | Subscribe to events, no need to subclass. |
| **SSL as decorator** | Same server, just add `SslOptions`. |
| **Integer session IDs** | `Interlocked.Increment` (fast, sortable) instead of Guid. |
| **Write serialization** | Per-session `SemaphoreSlim` lock prevents frame corruption. |

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
| [API Reference](docs/api-reference.md) | ISession, WebSocketSession, clients, middleware, framers |
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
