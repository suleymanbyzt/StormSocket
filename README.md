<p align="center">
  <img src="stormsocket.png" alt="StormSocket" width="256" />
</p>

<h1 align="center">StormSocket</h1>

<p align="center">
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-green.svg" alt="License" /></a>
  <a href="https://www.nuget.org/packages/StormSocket"><img src="https://img.shields.io/nuget/v/StormSocket.svg" alt="NuGet" /></a>
  <img src="https://img.shields.io/badge/.NET-6.0%20|%207.0%20|%208.0%20|%209.0%20|%2010.0-blue" alt=".NET" />
</p>

<p align="center">Modern, high-performance, event-based TCP/WebSocket/SSL server library for .NET built on <b>System.IO.Pipelines</b>.</p>

Zero subclassing required. Subscribe to events, configure options, and go.

# Contents
- [Features](#features)
- [Architecture](#architecture)
- [Requirements](#requirements)
- [Quick Start](#quick-start)
- [Examples](#examples)
  - [TCP Echo Server](#tcp-echo-server)
  - [WebSocket Chat Server](#websocket-chat-server)
  - [SSL/TLS Server](#ssltls-server)
  - [Message Framing](#message-framing)
  - [Session Management](#session-management)
  - [Groups & Rooms](#groups--rooms)
  - [Middleware Pipeline](#middleware-pipeline)
  - [WebSocket Heartbeat & Dead Connection Detection](#websocket-heartbeat--dead-connection-detection)
  - [Backpressure & Buffer Limits](#backpressure--buffer-limits)
  - [Full WebSocket Server with Admin Console](#full-websocket-server-with-admin-console)
- [Configuration Reference](#configuration-reference)
  - [ServerOptions](#serveroptions)
  - [WebSocketOptions](#websocketoptions)
  - [SslOptions](#ssloptions)
- [API Reference](#api-reference)
  - [ISession](#isession)
  - [WebSocketSession](#websocketsession)
  - [SessionManager](#sessionmanager)
  - [SessionGroup](#sessiongroup)
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
- **TCP Server** with optional message framing (raw, length-prefix, delimiter)
- **WebSocket Server** with full RFC 6455 compliance (text, binary, ping/pong, close status codes)
- **SSL/TLS** as a simple configuration option on any server
- **System.IO.Pipelines** for zero-copy I/O with built-in backpressure
- **Automatic heartbeat** with configurable ping interval and dead connection detection (missed pong counting)
- **Session management** - track, query, broadcast, and kick connections
- **Groups/Rooms** - named groups for targeted broadcast (chat rooms, game lobbies, etc.)
- **Middleware pipeline** - intercept connect, disconnect, data received, data sending, and errors
- **Backpressure & buffer limits** - configurable send/receive pipe limits prevent memory exhaustion
- **Thread-safe writes** - all PipeWriter access serialized via `SemaphoreSlim` (no frame corruption)
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
- **Send buffer full**: `SendAsync` awaits until the socket drains pending data. No data loss, no crash - just backpressure.
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
| `ReceiveBufferSize` | `int` | `65536` | OS socket receive buffer (bytes) |
| `SendBufferSize` | `int` | `65536` | OS socket send buffer (bytes) |
| `MaxPendingSendBytes` | `long` | `1048576` | Max bytes buffered before send backpressure (0 = unlimited) |
| `MaxPendingReceiveBytes` | `long` | `1048576` | Max bytes buffered before receive backpressure (0 = unlimited) |
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
    IReadOnlySet<string> Groups { get; }  // Group memberships

    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);
    ValueTask CloseAsync(CancellationToken ct = default);
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

## Write Serialization

All writes to a WebSocket connection are serialized through a per-session `SemaphoreSlim`:

```
session.SendTextAsync("hello")  ─┐
heartbeat ping                   ├─→ _writeLock → PipeWriter → Socket
auto-pong                        ─┘
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
- **Send pipe full**: `FlushAsync` awaits → caller waits → no memory growth

Resume happens at 50% of the threshold to avoid oscillation.

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
