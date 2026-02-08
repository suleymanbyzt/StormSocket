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

Zero subclassing required. Subscribe to events, configure options, and go. Server and client included.

# Contents
- [Features](#features)
- [Architecture](#architecture)
- [Benchmarks](#benchmarks)
- [Requirements](#requirements)
- [Quick Start](#quick-start)
- [Examples](#examples)
  - [TCP Echo Server](#tcp-echo-server)
  - [WebSocket Chat Server](#websocket-chat-server)
  - [SSL/TLS Server](#ssltls-server)
  - [TCP Client](#tcp-client)
  - [WebSocket Client](#websocket-client)
  - [Message Framing](#message-framing)
  - [Session Management](#session-management)
  - [Groups & Rooms](#groups--rooms)
  - [Middleware Pipeline](#middleware-pipeline)
  - [WebSocket Heartbeat & Dead Connection Detection](#websocket-heartbeat--dead-connection-detection)
  - [Slow Consumer Detection](#slow-consumer-detection)
  - [Closing Connections: CloseAsync vs Abort](#closing-connections-closeasync-vs-abort)
  - [Backpressure & Buffer Limits](#backpressure--buffer-limits)
  - [Full WebSocket Server with Admin Console](#full-websocket-server-with-admin-console)
- [Configuration Reference](#configuration-reference)
  - [ServerOptions](#serveroptions)
  - [ClientOptions](#clientoptions)
  - [WsClientOptions](#wsclientoptions)
  - [WebSocketOptions](#websocketoptions)
  - [SslOptions](#ssloptions)
- [API Reference](#api-reference)
  - [ISession](#isession)
  - [WebSocketSession](#websocketsession)
  - [SessionManager](#sessionmanager)
  - [SessionGroup](#sessiongroup)
  - [StormTcpClient](#stormtcpclient)
  - [StormWebSocketClient](#stormwebsocketclient)
  - [IConnectionMiddleware](#iconnectionmiddleware)
  - [Message Framers](#message-framers)
  - [WsMessage](#wsmessage)
- [How It Works](#how-it-works)
  - [Connection Lifecycle](#connection-lifecycle)
  - [Write Serialization](#write-serialization)
  - [Backpressure](#backpressure)
- [Building](#building)
- [Samples](#samples)
- [License](#license)

# Features
- **Event-based API** - no subclassing, just `server.OnDataReceived += handler`
- **TCP Server & Client** with optional message framing (raw, length-prefix, delimiter)
- **WebSocket Server & Client** with full RFC 6455 compliance (text, binary, ping/pong, close, client-side masking)
- **SSL/TLS** as a simple configuration option on any server or client
- **Auto-reconnect** - clients automatically reconnect on disconnect with configurable delay and max attempts
- **System.IO.Pipelines** for zero-copy I/O with built-in backpressure
- **Automatic heartbeat** with configurable ping interval and dead connection detection (missed pong counting)
- **Session management** - track, query, broadcast, and kick connections
- **Groups/Rooms** - named groups for targeted broadcast (chat rooms, game lobbies, etc.)
- **Middleware pipeline** - intercept connect, disconnect, data received, data sending, and errors (works on both server and client)
- **Backpressure & buffer limits** - configurable send/receive pipe limits prevent memory exhaustion
- **Slow consumer detection** - `SlowConsumerPolicy` per session: `Wait` (block), `Drop` (skip), or `Disconnect` (close). Applied to both broadcast and individual sends
- **Concurrent broadcast** - sends to all sessions in parallel, one slow client never blocks others
- **Max connections** - configurable limit, excess connections are immediately rejected
- **Thread-safe writes** - all PipeWriter access serialized via `SemaphoreSlim` (no frame corruption)
- **TCP Keep-Alive** - enabled by default, prevents idle connections from being silently dropped by firewalls and NATs
- **Connection timeout** - configurable timeout for client connections
- **Socket error handling** - proper SocketError filtering (ConnectionReset, Abort, etc.)
- **Multi-target**: net6.0, net7.0, net8.0, net9.0, net10.0
- **Zero dependencies** beyond `System.IO.Pipelines`

# Architecture

StormSocket is designed around a few core principles:

| Principle | How |
|---|---|
| **Composition over inheritance** | Flat structure, no deep inheritance chains. WebSocket doesn't inherit from HTTP. |
| **System.IO.Pipelines** | Zero-copy I/O with kernel-level backpressure, not custom Buffer classes. |
| **Event-based API** | Subscribe to events, no need to subclass `TcpSession` or override methods. |
| **SSL as decorator** | Same server, just add `SslOptions`. No separate `SslServer` class. |
| **Integer session IDs** | `Interlocked.Increment` (fast, sortable) instead of Guid. |
| **Write serialization** | All writes go through a per-session `SemaphoreSlim` lock - heartbeat pings, auto-pong, user sends, close frames never corrupt each other. |

# Benchmarks

Echo round-trip benchmark: 100 concurrent clients, 32-byte messages, 10-second sustained load.

Benchmark methodology and client/server structure inspired by [NetCoreServer](https://github.com/chronoxor/NetCoreServer) — a mature, battle-tested networking library that has been in active development for years. We use it as our reference point and recommend checking it out if you need a proven, stable solution.

StormSocket is newer and still evolving, but is designed to be production-ready from day one.

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

> Results will vary by hardware, OS, and .NET version. Benchmark projects are included under `benchmark/` — run them yourself to verify on your own setup.

### Reproduce

```bash
# Terminal 1: Start server
dotnet run -c Release --project benchmark/StormSocket.Benchmark.TcpEchoServer

# Terminal 2: Run client
dotnet run -c Release --project benchmark/StormSocket.Benchmark.TcpEchoClient -- -c 100 -m 1000 -s 32 -z 10
```

Replace `TcpEcho` with `WsEcho` for WebSocket benchmarks.

# Requirements
- [.NET 6.0](https://dotnet.microsoft.com/download/dotnet/6.0) or later
- No native dependencies

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

# Examples

## TCP Echo Server

The simplest possible server. Echoes back everything it receives.

```csharp
using System.Net;
using StormSocket.Server;

var server = new StormTcpServer(new ServerOptions
{
    EndPoint = new IPEndPoint(IPAddress.Any, 5000),
    NoDelay = true,
});

server.OnConnected += async session =>
{
    Console.WriteLine($"[{session.Id}] Connected ({server.Sessions.Count} online)");
};

server.OnDisconnected += async session =>
{
    Console.WriteLine($"[{session.Id}] Disconnected");
};

server.OnDataReceived += async (session, data) =>
{
    Console.WriteLine($"[{session.Id}] {data.Length} bytes received");
    await session.SendAsync(data);
};

server.OnError += async (session, ex) =>
{
    Console.WriteLine($"[{session?.Id}] Error: {ex.Message}");
};

await server.StartAsync();
Console.WriteLine("TCP Echo listening on :5000");
Console.ReadLine();
await server.DisposeAsync();
```

Test with: `telnet localhost 5000`

## WebSocket Chat Server

Broadcasts every message to all connected WebSocket clients.

```csharp
using System.Net;
using StormSocket.Server;

var ws = new StormWebSocketServer(new ServerOptions
{
    EndPoint = new IPEndPoint(IPAddress.Any, 8080),
    WebSocket = new WebSocketOptions
    {
        PingInterval = TimeSpan.FromSeconds(15),
        MaxMissedPongs = 3,
    },
});

ws.OnConnected += async session =>
{
    Console.WriteLine($"[{session.Id}] WebSocket connected");
};

ws.OnMessageReceived += async (session, msg) =>
{
    if (msg.IsText)
    {
        Console.WriteLine($"[{session.Id}] {msg.Text}");
        // Broadcast to everyone except sender
        await ws.BroadcastTextAsync(msg.Text, excludeId: session.Id);
    }
};

await ws.StartAsync();
Console.WriteLine("WebSocket Chat listening on :8080");
Console.ReadLine();
await ws.DisposeAsync();
```

Test with: `wscat -c ws://localhost:8080`

## SSL/TLS Server

Any server can be upgraded to SSL by adding `SslOptions`. No separate class needed.

```csharp
using System.Net;
using System.Security.Cryptography.X509Certificates;
using StormSocket.Server;

var server = new StormTcpServer(new ServerOptions
{
    EndPoint = new IPEndPoint(IPAddress.Any, 5001),
    Ssl = new SslOptions
    {
        Certificate = X509CertificateLoader.LoadPkcs12FromFile("server.pfx", "password"),
    },
});

server.OnDataReceived += async (session, data) => await session.SendAsync(data);

await server.StartAsync();
```

Works the same with `StormWebSocketServer` for WSS (WebSocket Secure).

## TCP Client

Connect to a TCP server with auto-reconnect, framing, and middleware support.

```csharp
using System.Net;
using System.Text;
using StormSocket.Client;

var client = new StormTcpClient(new ClientOptions
{
    EndPoint = new IPEndPoint(IPAddress.Loopback, 5000),
    NoDelay = true,
    AutoReconnect = true,
    ReconnectDelay = TimeSpan.FromSeconds(2),
});

client.OnConnected += async () =>
{
    Console.WriteLine("Connected to server!");
};

client.OnDataReceived += async data =>
{
    Console.WriteLine($"Received: {Encoding.UTF8.GetString(data.Span)}");
};

client.OnDisconnected += async () =>
{
    Console.WriteLine("Disconnected from server");
};

client.OnReconnecting += async (attempt, delay) =>
{
    Console.WriteLine($"Reconnecting (attempt #{attempt})...");
};

await client.ConnectAsync();
await client.SendAsync(Encoding.UTF8.GetBytes("Hello Server!"));
```

Use the same `IMessageFramer` on both server and client for message boundaries:

```csharp
var framer = new LengthPrefixFramer();

// Server
var server = new StormTcpServer(new ServerOptions { Framer = framer });

// Client
var client = new StormTcpClient(new ClientOptions { Framer = framer });
```

## WebSocket Client

Connect to any WebSocket server with automatic masking, heartbeat, and reconnect.

```csharp
using StormSocket.Client;

var ws = new StormWebSocketClient(new WsClientOptions
{
    Uri = new Uri("ws://localhost:8080/chat"),
    AutoReconnect = true,
    PingInterval = TimeSpan.FromSeconds(15),
});

ws.OnConnected += async () =>
{
    Console.WriteLine("WebSocket connected!");
    await ws.SendTextAsync("Hello from StormSocket!");
};

ws.OnMessageReceived += async msg =>
{
    if (msg.IsText)
        Console.WriteLine($"Server says: {msg.Text}");
    else
        Console.WriteLine($"Binary data: {msg.Data.Length} bytes");
};

ws.OnDisconnected += async () =>
{
    Console.WriteLine("WebSocket disconnected");
};

await ws.ConnectAsync();
```

For WSS (WebSocket Secure), use the `wss://` scheme:

```csharp
var ws = new StormWebSocketClient(new WsClientOptions
{
    Uri = new Uri("wss://echo.websocket.org"),
});
```

Send custom HTTP headers during the upgrade handshake:

```csharp
var ws = new StormWebSocketClient(new WsClientOptions
{
    Uri = new Uri("ws://localhost:8080"),
    Headers = new Dictionary<string, string>
    {
        { "Authorization", "Bearer my-token" },
    },
});
```

## Message Framing

TCP is a stream protocol - it doesn't preserve message boundaries. StormSocket provides pluggable framers to solve this.

### Length-Prefix Framer
Prepends a 4-byte big-endian length header to each message. Best for binary protocols.

```csharp
var server = new StormTcpServer(new ServerOptions
{
    EndPoint = new IPEndPoint(IPAddress.Any, 5000),
    Framer = new LengthPrefixFramer(), // [4-byte length][payload]
});

server.OnDataReceived += async (session, data) =>
{
    // data is a complete message - no partial reads
    Console.WriteLine($"Complete message: {data.Length} bytes");
};
```

### Delimiter Framer
Splits messages on a delimiter byte. Default is `\n`. Good for text protocols.

```csharp
var server = new StormTcpServer(new ServerOptions
{
    EndPoint = new IPEndPoint(IPAddress.Any, 5000),
    Framer = new DelimiterFramer(), // splits on \n
});
```

### Custom Framer
Implement `IMessageFramer` for your own protocol:

```csharp
public interface IMessageFramer
{
    bool TryReadMessage(ref ReadOnlySequence<byte> buffer, out ReadOnlyMemory<byte> message);
    void WriteFrame(IBufferWriter<byte> writer, ReadOnlySpan<byte> payload);
}
```

## Session Management

Every connection gets an `ISession` with a unique auto-incrementing ID.

```csharp
// Get session count
int online = server.Sessions.Count;

// Find a session by ID
if (server.Sessions.TryGet(42, out ISession? session))
{
    Console.WriteLine($"Session #{session.Id}, uptime: {session.Metrics.Uptime}");
    Console.WriteLine($"Bytes sent: {session.Metrics.BytesSent}");
    Console.WriteLine($"Bytes received: {session.Metrics.BytesReceived}");
    Console.WriteLine($"Groups: {string.Join(", ", session.Groups)}");
}

// Broadcast to all
await server.BroadcastAsync(data);

// Broadcast excluding one
await server.BroadcastAsync(data, excludeId: senderId);

// Kick a session
await session.CloseAsync();

// Iterate all sessions
foreach (ISession s in server.Sessions.All)
{
    Console.WriteLine($"#{s.Id} - {s.State} - up {s.Metrics.Uptime:hh\\:mm\\:ss}");
}
```

### WebSocket-specific methods

Cast to `WebSocketSession` for text frame support:

```csharp
ws.OnMessageReceived += async (session, msg) =>
{
    if (session is WebSocketSession wss)
    {
        // Send text frame (UTF-8)
        await wss.SendTextAsync("Hello!");

        // Send pre-encoded UTF-8 bytes (zero-copy)
        byte[] utf8 = Encoding.UTF8.GetBytes("Hello!");
        await wss.SendTextAsync(utf8);

        // Send binary frame
        await wss.SendAsync(binaryData);
    }
};
```

## Groups & Rooms

Built-in named groups for chat rooms, game lobbies, pub/sub channels, etc.

```csharp
// Add session to a group
server.Groups.Add("room:general", session);
server.Groups.Add("room:vip", session);

// Broadcast to a group
await server.Groups.BroadcastAsync("room:general", data);

// Broadcast excluding sender
await server.Groups.BroadcastAsync("room:general", data, excludeId: session.Id);

// Remove from group
server.Groups.Remove("room:vip", session);

// Check member count
int count = server.Groups.MemberCount("room:general");

// List all groups
foreach (string name in server.Groups.GroupNames)
{
    Console.WriteLine($"{name}: {server.Groups.MemberCount(name)} members");
}

// Auto-cleanup on disconnect (call in OnDisconnected)
server.Groups.RemoveFromAll(session);
```

## Middleware Pipeline

Intercept and modify data flow, log connections, implement rate limiting, authentication, etc.

```csharp
public class AuthMiddleware : IConnectionMiddleware
{
    public ValueTask OnConnectedAsync(ISession session)
    {
        Console.WriteLine($"#{session.Id} connected");
        return ValueTask.CompletedTask;
    }

    public ValueTask<ReadOnlyMemory<byte>> OnDataReceivedAsync(ISession session, ReadOnlyMemory<byte> data)
    {
        // Return data to pass downstream, or ReadOnlyMemory<byte>.Empty to suppress
        if (!IsAuthenticated(session))
            return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty); // drop message

        return ValueTask.FromResult(data);
    }

    public ValueTask OnDisconnectedAsync(ISession session)
    {
        // Called in reverse order (like middleware unwinding)
        return ValueTask.CompletedTask;
    }

    public ValueTask OnErrorAsync(ISession session, Exception exception)
    {
        Console.WriteLine($"#{session.Id} error: {exception.Message}");
        return ValueTask.CompletedTask;
    }
}

server.UseMiddleware(new AuthMiddleware());
```

All methods have default no-op implementations - override only what you need.

## WebSocket Heartbeat & Dead Connection Detection

StormSocket automatically sends WebSocket Ping frames at a configurable interval and tracks Pong responses. If a client misses too many consecutive pongs, the connection is considered dead and automatically closed.

```csharp
var ws = new StormWebSocketServer(new ServerOptions
{
    WebSocket = new WebSocketOptions
    {
        PingInterval = TimeSpan.FromSeconds(15),  // Send Ping every 15s
        MaxMissedPongs = 3,                        // Allow 3 missed Pongs
        AutoPong = true,                           // Auto-reply to client Pings
    },
});
```

**How it works:**
```
t=0s   : Connection established
t=15s  : Server sends Ping → missedPongs=1
t=17s  : Client sends Pong → missedPongs=0 (reset)
t=30s  : Server sends Ping → missedPongs=1
t=45s  : Server sends Ping → missedPongs=2 (client not responding)
t=60s  : Server sends Ping → missedPongs=3
t=75s  : missedPongs=4 > 3 → OnTimeout → connection closed
```

## Slow Consumer Detection

When broadcasting to thousands of clients, one slow client can stall delivery to everyone else. `SlowConsumerPolicy` solves this at the session level - it applies to **both broadcast and individual sends**.

```csharp
using StormSocket.Core;

var server = new StormWebSocketServer(new ServerOptions
{
    EndPoint = new IPEndPoint(IPAddress.Any, 8080),
    MaxConnections = 50_000,
    SlowConsumerPolicy = SlowConsumerPolicy.Drop,
});
```

| Policy | Behavior | Use Case |
|---|---|---|
| `Wait` | Awaits until pipe drains (default) | Reliable delivery, low client count |
| `Drop` | Silently skips backpressured sessions | Real-time data where stale data is useless (chat, game state, stock tickers) |
| `Disconnect` | Closes backpressured sessions | Critical feeds where all clients must keep up (financial data, command streams) |

The policy is enforced inside `SendAsync` / `SendTextAsync`, so it works everywhere:

```csharp
// Individual send - policy applies
await session.SendAsync(data);

// Broadcast - policy applies per session, sends are concurrent
await server.BroadcastAsync(data);
await ws.BroadcastTextAsync("update");

// Check if a session is under pressure
if (session.IsBackpressured)
{
    Console.WriteLine($"Session #{session.Id} is slow");
}
```

**How it works with 1M clients:**
```
Broadcast("data") called
    │
    ├─ Session #1: not backpressured → send (concurrent)
    ├─ Session #2: backpressured + Drop → skip
    ├─ Session #3: not backpressured → send (concurrent)
    ├─ Session #4: backpressured + Disconnect → close + skip
    └─ ... all sessions checked in parallel
```

All broadcast sends are dispatched concurrently. Each session has its own pipe, so one slow flush never blocks others.

### Max Connections

Limit concurrent connections. Excess connections are rejected at the TCP level (socket closed immediately before any handshake).

```csharp
var server = new StormTcpServer(new ServerOptions
{
    MaxConnections = 10_000, // 0 = unlimited (default)
});
```

## Closing Connections: CloseAsync vs Abort

StormSocket provides two ways to close a connection:

| Method | Behavior |
|---|---|
| `CloseAsync()` | **Graceful**: Sends a WebSocket Close frame (if WS), waits for flush, then closes the socket. Safe and clean, but can block if the client is slow. |
| `Abort()` | **Immediate**: Closes the socket directly without sending anything. All pending reads and writes are cancelled. The client sees a connection reset. |

**When to use Abort:**

If a client is so slow that it can't even process a Close frame, `CloseAsync()` will block waiting for the flush to complete. This is the exact scenario you encounter with slow consumers in production — the client's receive buffer is full, your Close frame sits in the send pipe, and the connection stays open indefinitely.

`Abort()` skips the Close frame entirely and terminates the socket immediately. The server's read loop breaks, `OnDisconnected` fires, and the session is cleaned up.

```csharp
// Graceful close - sends Close frame, waits for flush
await session.CloseAsync();

// Immediate termination - no Close frame, no waiting
session.Abort();

// Common pattern: try graceful, fall back to abort
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
try
{
    await session.CloseAsync(cts.Token);
}
catch (OperationCanceledException)
{
    session.Abort(); // client didn't close in time
}
```

`SlowConsumerPolicy.Disconnect` uses `Abort()` internally — when backpressure is detected, the socket is killed immediately without attempting a graceful close.

## Backpressure & Buffer Limits

Pipe-level backpressure prevents memory exhaustion from slow consumers or fast producers.

```csharp
var server = new StormTcpServer(new ServerOptions
{
    MaxPendingSendBytes = 1024 * 1024,     // 1 MB send buffer limit
    MaxPendingReceiveBytes = 1024 * 1024,  // 1 MB receive buffer limit
});
```

**What happens when limits are reached:**
- **Send buffer full**: Behavior depends on `SlowConsumerPolicy`:
  - `Wait` (default): `SendAsync` awaits until the socket drains pending data
  - `Drop`: `SendAsync` returns immediately, message is silently discarded
  - `Disconnect`: Session is closed automatically
- **Receive buffer full**: Socket reads pause until the application processes buffered messages. The OS TCP window handles flow control upstream.

Set to `0` for unlimited (not recommended for production).

## Full WebSocket Server with Admin Console

See `samples/StormSocket.Samples.WsServer` for a complete example featuring:

- JSON command protocol (setName, chat, whisper, join/leave rooms, list users, etc.)
- Per-second heartbeat tick with timestamp broadcast to all clients
- Admin console with `/sessions`, `/kick`, `/broadcast`, `/rooms`, `/info` commands
- Middleware-based connection logging
- Session management with user names and room membership

```bash
dotnet run --project samples/StormSocket.Samples.WsServer
```

```
StormSocket WsServer running on ws://0.0.0.0:8080
/sessions  /kick <id>  /broadcast <msg>
/rooms     /info <id>  /stop
```

# Configuration Reference

## ServerOptions

| Property | Type | Default | Description |
|---|---|---|---|
| `EndPoint` | `IPEndPoint` | `0.0.0.0:5000` | IP and port to listen on |
| `Backlog` | `int` | `128` | Maximum pending connection queue |
| `NoDelay` | `bool` | `false` | Disable Nagle's algorithm for lower latency |
| `KeepAlive` | `bool` | `true` | Enable TCP Keep-Alive to prevent idle connections from being dropped by firewalls/NATs |
| `ReceiveBufferSize` | `int` | `65536` | OS socket receive buffer (bytes) |
| `SendBufferSize` | `int` | `65536` | OS socket send buffer (bytes) |
| `MaxPendingSendBytes` | `long` | `1048576` | Max bytes buffered before send backpressure (0 = unlimited) |
| `MaxPendingReceiveBytes` | `long` | `1048576` | Max bytes buffered before receive backpressure (0 = unlimited) |
| `MaxConnections` | `int` | `0` | Max concurrent connections (0 = unlimited). Excess are rejected at TCP level |
| `SlowConsumerPolicy` | `SlowConsumerPolicy` | `Wait` | Behavior when a session is backpressured: `Wait`, `Drop`, or `Disconnect` |
| `Ssl` | `SslOptions?` | `null` | SSL/TLS configuration (null = plain TCP) |
| `WebSocket` | `WebSocketOptions?` | `null` | WebSocket settings (only for StormWebSocketServer) |
| `Framer` | `IMessageFramer?` | `null` | Message framing strategy (null = raw bytes) |

## WebSocketOptions

| Property | Type | Default | Description |
|---|---|---|---|
| `PingInterval` | `TimeSpan` | `30s` | Interval between Ping frames (`TimeSpan.Zero` = disabled) |
| `MaxMissedPongs` | `int` | `3` | Consecutive missed Pongs before closing |
| `MaxFrameSize` | `int` | `1048576` | Maximum frame payload (bytes). Oversized frames trigger close with `MessageTooBig` |
| `AutoPong` | `bool` | `true` | Automatically reply to client Ping frames |

## ClientOptions

| Property | Type | Default | Description |
|---|---|---|---|
| `EndPoint` | `IPEndPoint` | `127.0.0.1:5000` | Server endpoint to connect to |
| `NoDelay` | `bool` | `false` | Disable Nagle's algorithm |
| `KeepAlive` | `bool` | `true` | Enable TCP Keep-Alive to prevent idle drops |
| `MaxPendingSendBytes` | `long` | `1048576` | Max send buffer before backpressure |
| `MaxPendingReceiveBytes` | `long` | `1048576` | Max receive buffer before backpressure |
| `Ssl` | `ClientSslOptions?` | `null` | SSL/TLS configuration |
| `Framer` | `IMessageFramer?` | `null` | Message framing strategy |
| `AutoReconnect` | `bool` | `false` | Auto-reconnect on disconnect |
| `ReconnectDelay` | `TimeSpan` | `2s` | Delay between reconnect attempts |
| `MaxReconnectAttempts` | `int` | `0` | Max attempts (0 = unlimited) |
| `ConnectTimeout` | `TimeSpan` | `10s` | Connection timeout |

## WsClientOptions

| Property | Type | Default | Description |
|---|---|---|---|
| `Uri` | `Uri` | `ws://localhost:8080` | WebSocket URI (`ws://` or `wss://`) |
| `NoDelay` | `bool` | `false` | Disable Nagle's algorithm |
| `KeepAlive` | `bool` | `true` | Enable TCP Keep-Alive to prevent idle drops |
| `MaxFrameSize` | `int` | `1048576` | Maximum frame payload (bytes) |
| `PingInterval` | `TimeSpan` | `30s` | Ping interval (`TimeSpan.Zero` = disabled) |
| `MaxMissedPongs` | `int` | `3` | Max missed Pongs before timeout |
| `AutoPong` | `bool` | `true` | Auto-reply to server Pings |
| `AutoReconnect` | `bool` | `false` | Auto-reconnect on disconnect |
| `ReconnectDelay` | `TimeSpan` | `2s` | Delay between reconnect attempts |
| `MaxReconnectAttempts` | `int` | `0` | Max attempts (0 = unlimited) |
| `ConnectTimeout` | `TimeSpan` | `10s` | Connection timeout |
| `Headers` | `Dictionary<string, string>?` | `null` | Extra HTTP headers for upgrade |
| `Ssl` | `ClientSslOptions?` | `null` | SSL/TLS configuration |

## SslOptions

| Property | Type | Default | Description |
|---|---|---|---|
| `Certificate` | `X509Certificate2` | *(required)* | Server certificate |
| `Protocols` | `SslProtocols` | `Tls12 \| Tls13` | Allowed TLS protocol versions |
| `ClientCertificateRequired` | `bool` | `false` | Require client certificate |

# API Reference

## ISession

```csharp
public interface ISession : IAsyncDisposable
{
    long Id { get; }                      // Unique auto-incrementing ID
    ConnectionState State { get; }        // Connected, Closing, Closed
    ConnectionMetrics Metrics { get; }    // BytesSent, BytesReceived, Uptime
    bool IsBackpressured { get; }         // True when send buffer is full
    IReadOnlySet<string> Groups { get; }  // Group memberships

    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);
    ValueTask CloseAsync(CancellationToken ct = default);
    void Abort();
    void JoinGroup(string group);
    void LeaveGroup(string group);
}
```

## WebSocketSession

Extends ISession with WebSocket-specific methods:

```csharp
// Text frame from string
await session.SendTextAsync("Hello!");

// Text frame from pre-encoded UTF-8 (zero-copy)
await session.SendTextAsync(utf8Bytes);

// Binary frame
await session.SendAsync(binaryData);

// Close with status
await session.CloseAsync();
```

## SessionManager

```csharp
server.Sessions.Count;                              // Current count
server.Sessions.All;                                 // IEnumerable<ISession>
server.Sessions.TryGet(id, out ISession? session);   // Lookup by ID
server.Sessions.BroadcastAsync(data);                // Send to all
server.Sessions.CloseAllAsync();                     // Graceful shutdown
```

## SessionGroup

```csharp
server.Groups.Add("room", session);                  // Join
server.Groups.Remove("room", session);               // Leave
server.Groups.RemoveFromAll(session);                // Leave all (on disconnect)
server.Groups.BroadcastAsync("room", data);          // Send to room
server.Groups.MemberCount("room");                   // Count
server.Groups.GroupNames;                             // All room names
```

## StormTcpClient

```csharp
var client = new StormTcpClient(options);

// Events
client.OnConnected += async () => { };
client.OnDisconnected += async () => { };
client.OnDataReceived += async (ReadOnlyMemory<byte> data) => { };
client.OnError += async (Exception ex) => { };
client.OnReconnecting += async (int attempt, TimeSpan delay) => { };

// Lifecycle
await client.ConnectAsync(ct);             // Connect (with optional CancellationToken)
await client.SendAsync(data, ct);          // Send data
await client.DisconnectAsync(ct);          // Graceful disconnect
await client.DisposeAsync();               // Disconnect + cleanup

// Properties
client.State;                              // ConnectionState (Connecting, Connected, Closing, Closed)
client.Metrics.BytesSent;                  // Total bytes sent
client.Metrics.BytesReceived;              // Total bytes received
client.Metrics.Uptime;                     // Connection uptime
client.UseMiddleware(middleware);           // Add middleware
```

## StormWebSocketClient

```csharp
var ws = new StormWebSocketClient(options);

// Events
ws.OnConnected += async () => { };
ws.OnDisconnected += async () => { };
ws.OnMessageReceived += async (WsMessage msg) => { };
ws.OnError += async (Exception ex) => { };
ws.OnReconnecting += async (int attempt, TimeSpan delay) => { };

// Lifecycle
await ws.ConnectAsync(ct);                 // Connect + WebSocket upgrade
await ws.SendTextAsync("hello", ct);       // Send text frame (masked)
await ws.SendTextAsync(utf8Bytes, ct);     // Send pre-encoded text frame
await ws.SendAsync(binaryData, ct);        // Send binary frame (masked)
await ws.DisconnectAsync(ct);              // Send Close frame + disconnect
await ws.DisposeAsync();                   // Disconnect + cleanup

// Properties
ws.State;                                  // ConnectionState
ws.Metrics;                                // ConnectionMetrics
ws.RemoteEndPoint;                         // Server endpoint
ws.UseMiddleware(middleware);               // Add middleware
```

## IConnectionMiddleware

```csharp
public interface IConnectionMiddleware
{
    ValueTask OnConnectedAsync(ISession session);
    ValueTask<ReadOnlyMemory<byte>> OnDataReceivedAsync(ISession session, ReadOnlyMemory<byte> data);
    ValueTask<ReadOnlyMemory<byte>> OnDataSendingAsync(ISession session, ReadOnlyMemory<byte> data);
    ValueTask OnDisconnectedAsync(ISession session);  // Called in reverse order
    ValueTask OnErrorAsync(ISession session, Exception exception);
}
```

All methods have default no-op implementations.

## Message Framers

| Framer | Format | Use Case |
|---|---|---|
| `RawFramer` | No framing, pass-through | Raw TCP streams |
| `LengthPrefixFramer` | `[4-byte BE length][payload]` | Binary protocols |
| `DelimiterFramer` | `[payload][delimiter]` (default: `\n`) | Text protocols, line-based |
| Custom `IMessageFramer` | Your format | Custom protocols |

## WsMessage

```csharp
public readonly struct WsMessage
{
    ReadOnlyMemory<byte> Data { get; }  // Raw payload
    bool IsText { get; }                // True = Text frame, False = Binary
    string Text { get; }                // UTF-8 decode (throws if !IsText)
}
```

# How It Works

## Connection Lifecycle

**Server-side:**
```
Client connects
    │
    ├─ Socket.AcceptAsync
    ├─ TcpTransport created (Pipe pair with backpressure limits)
    ├─ SSL handshake (if SslOptions configured)
    ├─ WebSocket HTTP upgrade (if StormWebSocketServer)
    ├─ Session created, added to SessionManager
    ├─ Middleware.OnConnectedAsync
    ├─ OnConnected event
    │
    ├─ Read loop (receives frames/data, dispatches events)
    ├─ Heartbeat loop (sends Pings, tracks Pongs) [concurrent]
    │
    ├─ Connection closes (client disconnect / timeout / kick)
    ├─ Middleware.OnDisconnectedAsync (reverse order)
    ├─ OnDisconnected event
    ├─ Session removed from SessionManager
    └─ Transport disposed
```

**Client-side:**
```
ConnectAsync called
    │
    ├─ Socket.ConnectAsync (with timeout)
    ├─ TcpTransport or SslTransport created
    ├─ SSL handshake (if configured or wss://)
    ├─ WebSocket HTTP upgrade (if StormWebSocketClient)
    ├─ Middleware.OnConnectedAsync
    ├─ OnConnected event
    │
    ├─ Read/frame loop (dispatches OnDataReceived / OnMessageReceived)
    ├─ Heartbeat loop (WebSocket: sends masked Pings) [concurrent]
    │
    ├─ Connection closes (server disconnect / timeout / DisconnectAsync)
    ├─ Middleware.OnDisconnectedAsync (reverse order)
    ├─ OnDisconnected event
    │
    ├─ [AutoReconnect = true]: wait ReconnectDelay → OnReconnecting → retry
    └─ Transport disposed
```

## Write Serialization

All writes to a WebSocket connection are serialized through a per-session `SemaphoreSlim`:

```
session.SendTextAsync("hello")  ─┐
heartbeat ping                   ├─→ _writeLock → PipeWriter → Socket
auto-pong                       ─┘
```

This prevents frame interleaving when multiple sources write concurrently (user code, heartbeat timer, auto-pong handler).

## Backpressure

StormSocket uses `System.IO.Pipelines` with configurable `pauseWriterThreshold` and `resumeWriterThreshold`:

```
[Socket] → ReceivePipe (1MB limit) → [Frame Decoder] → [Event Handler]
[Event Handler] → [Frame Encoder] → SendPipe (1MB limit) → [Socket]
```

When a pipe fills up:
- **Receive pipe full**: `ReceiveAsync` pauses → OS TCP window closes → sender slows down
- **Send pipe full**: `session.IsBackpressured = true` → `SlowConsumerPolicy` kicks in:
  - `Wait`: `FlushAsync` awaits → caller waits → no memory growth
  - `Drop`: `SendAsync` returns immediately → message discarded → no blocking
  - `Disconnect`: `CloseAsync` fired → session removed → no slow consumer

Resume happens at 50% of the threshold to avoid oscillation.

**Broadcast is concurrent**: Each session's send runs in parallel. One slow session never blocks delivery to others, regardless of policy.

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
