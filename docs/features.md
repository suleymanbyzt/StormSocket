# Features Guide

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
if (server.Sessions.TryGet(42, out ISession? session) && session is not null)
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

### Per-Session User Data

Attach custom data (user ID, auth info, roles) directly to a session — no external dictionary needed.

**String keys** (simple, familiar):

```csharp
server.OnConnected += async (session) =>
{
    session.Items["userId"] = "abc123";
    session.Items["role"] = "admin";
};

server.OnMessageReceived += async (session, msg) =>
{
    string userId = (string)session.Items["userId"];
};
```

**Strongly-typed keys** (compile-time safe, no casts):

```csharp
static readonly SessionKey<string> UserId = new("userId");
static readonly SessionKey<string> Role = new("role");

server.OnConnected += async (session) =>
{
    session.Set(UserId, "abc123");
    session.Set(Role, "admin");
};

server.OnMessageReceived += async (session, msg) =>
{
    string userId = session.Get(UserId); // no cast needed
};
```

Both approaches share the same underlying store — use whichever fits. The dictionary is not thread-safe by design, since session events are sequential per-session.

### WebSocket-specific methods

WebSocket event handlers receive `IWebSocketSession` directly — no casting needed:

```csharp
ws.OnMessageReceived += async (session, msg) =>
{
    // session is IWebSocketSession — SendTextAsync is available directly
    await session.SendTextAsync("Hello!");

    // Send pre-encoded UTF-8 bytes (zero-copy)
    byte[] utf8 = Encoding.UTF8.GetBytes("Hello!");
    await session.SendTextAsync(utf8);

    // Send binary frame
    await session.SendAsync(binaryData);
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

    public ValueTask OnDisconnectedAsync(ISession session, DisconnectReason reason)
    {
        // Called in reverse order (like middleware unwinding)
        Console.WriteLine($"#{session.Id} disconnected: {reason}");
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
        Heartbeat = new HeartbeatOptions
        {
            PingInterval = TimeSpan.FromSeconds(15),  // Send Ping every 15s
            MaxMissedPongs = 3,                        // Allow 3 missed Pongs
            AutoPong = true,                           // Auto-reply to client Pings
        },
    },
});
```

**How it works:**
```
t=0s   : Connection established
t=15s  : Server sends Ping -> missedPongs=1
t=17s  : Client sends Pong -> missedPongs=0 (reset)
t=30s  : Server sends Ping -> missedPongs=1
t=45s  : Server sends Ping -> missedPongs=2 (client not responding)
t=60s  : Server sends Ping -> missedPongs=3
t=75s  : missedPongs=4 > 3 -> OnTimeout -> connection closed
```

## TCP Keep-Alive Fine-Tuning

TCP Keep-Alive detects dead connections at the OS level (cable unplugged, process crash, firewall timeout). By default, most operating systems wait **2 hours** before sending the first probe — too slow for production. StormSocket lets you tune this per socket:

```csharp
var server = new StormTcpServer(new ServerOptions
{
    EndPoint = new IPEndPoint(IPAddress.Any, 5000),
    Socket = new SocketTuningOptions
    {
        KeepAlive = true,                                    // default: true
        KeepAliveIdleTime = TimeSpan.FromMinutes(2),         // first probe after 2min idle
        KeepAliveProbeInterval = TimeSpan.FromSeconds(30),   // probe every 30s after that
        KeepAliveProbeCount = 3,                             // 3 failed probes -> connection dead
    },
});
```

**How it works:**
```
t=0s   : Last data exchanged
t=120s : No activity -> OS sends first Keep-Alive probe -> probes=1
t=150s : No response -> probe #2
t=180s : No response -> probe #3
t=210s : No response -> probe #4 > 3 -> OS closes socket -> OnDisconnected fires
```

This applies to both servers and clients — `SocketTuningOptions` is shared across `ServerOptions`, `ClientOptions`, and `WsClientOptions`. Set any property to `null` (default) to use the OS default for that specific setting.

> **Note:** TCP Keep-Alive detects **dead** connections (peer is unreachable). It does **not** detect idle-but-alive connections (peer is reachable but not sending data). For WebSocket, use [Heartbeat](#websocket-heartbeat--dead-connection-detection) for application-level liveness detection.

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
    |
    +- Session #1: not backpressured -> send (concurrent)
    +- Session #2: backpressured + Drop -> skip
    +- Session #3: not backpressured -> send (concurrent)
    +- Session #4: backpressured + Disconnect -> close + skip
    +- ... all sessions checked in parallel
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

## Rate Limiting

Built-in opt-in middleware that limits incoming messages per client within a configurable time window. Protects the server from misbehaving or malicious clients.

```csharp
using StormSocket.Middleware.RateLimiting;

var rateLimiter = new RateLimitMiddleware(new RateLimitOptions
{
    Window = TimeSpan.FromSeconds(10),   // Time window
    MaxMessages = 500,                   // Max messages per window
    Scope = RateLimitScope.Session,      // Per session (default) or per IP
    ExceededAction = RateLimitAction.Disconnect, // Disconnect (default) or Drop
});

rateLimiter.OnExceeded += async (session) =>
{
    Console.WriteLine($"Rate limit exceeded: #{session.Id} ({session.RemoteEndPoint})");
};

server.UseMiddleware(rateLimiter);
```

| Option | Values | Description |
|---|---|---|
| `Window` | `TimeSpan` | Time window for counting messages (default: 1 second) |
| `MaxMessages` | `int` | Max messages allowed within the window (default: 100) |
| `Scope` | `Session`, `IpAddress` | Per-session counters (default) or shared per IP |
| `ExceededAction` | `Disconnect`, `Drop` | Abort the connection (default) or silently drop the message |

**Scope options:**

- **`Session`** (default): Each session has its own independent counter. Safe and predictable.
- **`IpAddress`**: All sessions from the same IP share a single counter. Useful against distributed abuse from a single source. Note: clients behind NAT share an IP, so set limits accordingly.

**Action options:**

- **`Disconnect`** (default): Calls `session.Abort()` to immediately terminate the connection. Best for untrusted clients.
- **`Drop`**: Silently discards the message but keeps the connection open. Useful for trusted clients that occasionally burst.

The `OnExceeded` event fires on every rate limit hit (regardless of action), so you can log, monitor, or send a warning before the action is taken.

> **Note:** `Disconnect` uses `Abort()` (immediate TCP close) rather than `CloseAsync()` (graceful WebSocket Close frame). This is intentional — a client flooding the server may not process a Close frame, causing the same blocking issue that `Abort()` was designed to solve. If you need a more nuanced approach, use `Drop` action with the `OnExceeded` event to handle it yourself.

> **Using .NET's built-in rate limiting?** The `IConnectionMiddleware` interface is your extension point — you can create your own middleware wrapping `System.Threading.RateLimiting` (TokenBucketRateLimiter, SlidingWindowRateLimiter, etc.) without any extra dependencies from StormSocket.

## Message Fragmentation

StormSocket fully supports WebSocket message fragmentation ([RFC 6455 Section 5.4](https://www.rfc-editor.org/rfc/rfc6455.html#section-5.4)). Fragmented messages are automatically reassembled — your `OnMessageReceived` handler always receives the complete message.

**Receiving fragmented messages** requires zero code changes. The server and client transparently reassemble fragments:

```
Client sends: [Text FIN=0 "Hello "] -> [Continuation FIN=1 "World"]
Server receives: OnMessageReceived with "Hello World"
```

Control frames (Ping/Pong/Close) can be interleaved between fragments per the RFC — they are handled normally while reassembly continues.

**Sending fragmented messages** is available via the encoder:

```csharp
// Server-side: split a large message into 4KB fragments
await session.WriteFrameAsync(writer =>
    WsFrameEncoder.WriteFragmented(writer, WsOpCode.Text, largePayload, fragmentSize: 4096));

// Client-side: masked fragments
await client.WriteFrameAsync(writer =>
    WsFrameEncoder.WriteMaskedFragmented(writer, WsOpCode.Binary, data, fragmentSize: 4096));
```

**`MaxMessageSize`** limits the total reassembled message size (default: 4 MB). If exceeded, the connection is closed with `MessageTooBig` (1009):

```csharp
WebSocket = new WebSocketOptions
{
    MaxFrameSize = 1024 * 1024,   // 1 MB per frame
    MaxMessageSize = 4 * 1024 * 1024, // 4 MB total across fragments
}
```

**Protocol violations** are automatically detected and close the connection with `ProtocolError` (1002):
- Continuation frame without a preceding data frame
- New data frame while a fragmented message is in progress
- Fragmented control frame (control frames must not be fragmented)

## Permessage-Deflate Compression

StormSocket supports the `permessage-deflate` WebSocket extension ([RFC 7692](https://www.rfc-editor.org/rfc/rfc7692.html)) for transparent message compression. When enabled, text and binary messages are compressed using DEFLATE, typically reducing bandwidth by 60-80% for text-heavy payloads (JSON, chat messages).

```csharp
// Server
var server = new StormWebSocketServer(new ServerOptions
{
    EndPoint = new IPEndPoint(IPAddress.Any, 8080),
    WebSocket = new WebSocketOptions
    {
        Compression = new WsCompressionOptions
        {
            Enabled = true,                          // Default: false
            CompressionLevel = CompressionLevel.Fastest,
            MinMessageSize = 128,                    // Don't compress tiny messages
        },
    },
});

// Client
var client = new StormWebSocketClient(new WsClientOptions
{
    Uri = new Uri("ws://localhost:8080"),
    Compression = new WsCompressionOptions { Enabled = true },
});
```

**How it works:**
- During the WebSocket handshake, the extension is negotiated via `Sec-WebSocket-Extensions` headers
- If both sides support it, messages are automatically compressed on send and decompressed on receive
- Messages smaller than `MinMessageSize` are sent uncompressed (compression overhead would be wasteful)
- Control frames (Ping/Pong/Close) are never compressed per the RFC
- Fragmented messages are compressed as a whole before fragmentation, and decompressed after reassembly
- If only one side enables compression, the handshake gracefully falls back to uncompressed — no errors

**Context takeover:** By default, `ServerNoContextTakeover` and `ClientNoContextTakeover` are both `true`, meaning each message is compressed independently. This uses less memory but may reduce compression ratio for similar consecutive messages.

## Disconnect Reasons

Every `OnDisconnected` event includes a `DisconnectReason` explaining **why** the connection was closed. No more guessing from logs.

```csharp
// Server
server.OnDisconnected += async (session, reason) =>
{
    Console.WriteLine($"#{session.Id} disconnected: {reason}");
};

// Client
client.OnDisconnected += async (reason) =>
{
    Console.WriteLine($"Disconnected: {reason}");
};
```

The reason is also available on the session itself via `session.DisconnectReason`.

| Reason | When |
|---|---|
| `ClosedByClient` | Remote peer sent TCP FIN or WebSocket Close frame |
| `ClosedByServer` | Local side called `CloseAsync()` |
| `Aborted` | Local side called `Abort()` |
| `ProtocolError` | WebSocket protocol violation (RFC 6455) |
| `TransportError` | Socket/IO error (check `OnError` for details) |
| `HeartbeatTimeout` | Remote peer stopped responding to Pings |
| `HandshakeTimeout` | WebSocket upgrade not completed in time |
| `SlowConsumer` | Session couldn't keep up with outgoing data |
| `GoingAway` | Server is shutting down (RFC 6455 status 1001) |
| `RateLimited` | Rate limit exceeded with `Disconnect` action |
| `IdleTimeout` | No application-level data received within the configured idle timeout |
| `MessageTooBig` | Incoming message exceeds the configured `MaxMessageSize` — client is disconnected with RFC 6455 status 1009 |

> **Note:** The reason is set internally before `OnDisconnected` fires. When multiple reasons apply (e.g. heartbeat timeout triggers a `CloseAsync`), the first/most specific reason wins.

Middleware also receives the reason:

```csharp
public class LogMiddleware : IConnectionMiddleware
{
    public ValueTask OnDisconnectedAsync(ISession session, DisconnectReason reason)
    {
        _logger.LogInfo("#{Id} disconnected: {Reason}", session.Id, reason);
        return ValueTask.CompletedTask;
    }
}
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
    Socket = new SocketTuningOptions
    {
        MaxPendingSendBytes = 1024 * 1024,     // 1 MB send buffer limit
        MaxPendingReceiveBytes = 1024 * 1024,  // 1 MB receive buffer limit
    },
});
```

**What happens when limits are reached:**
- **Send buffer full**: Behavior depends on `SlowConsumerPolicy`:
  - `Wait` (default): `SendAsync` awaits until the socket drains pending data
  - `Drop`: `SendAsync` returns immediately, message is silently discarded
  - `Disconnect`: Session is closed automatically
- **Receive buffer full**: Socket reads pause until the application processes buffered messages. The OS TCP window handles flow control upstream.

Set to `0` for unlimited (not recommended for production).

## Server Metrics

Both TCP and WebSocket servers expose a `server.Metrics` property with server-wide aggregate counters. Built on `System.Diagnostics.Metrics`, so it integrates with OpenTelemetry, Prometheus, and `dotnet-counters` with zero custom bridging code.

### Direct property access

```csharp
var m = server.Metrics;
Console.WriteLine($"Active: {m.ActiveConnections}");
Console.WriteLine($"Total:  {m.TotalConnections}");
Console.WriteLine($"Msgs ↑: {m.MessagesSent}");
Console.WriteLine($"Msgs ↓: {m.MessagesReceived}");
Console.WriteLine($"Bytes ↑: {m.BytesSentTotal}");
Console.WriteLine($"Bytes ↓: {m.BytesReceivedTotal}");
Console.WriteLine($"Errors: {m.ErrorCount}");
```

### Periodic console logging

```csharp
_ = Task.Run(async () =>
{
    while (true)
    {
        await Task.Delay(TimeSpan.FromSeconds(10));
        var m = server.Metrics;
        Console.WriteLine(
            $"[metrics] active={m.ActiveConnections} total={m.TotalConnections} " +
            $"msg_in={m.MessagesReceived} msg_out={m.MessagesSent} " +
            $"bytes_in={m.BytesReceivedTotal} bytes_out={m.BytesSentTotal} " +
            $"errors={m.ErrorCount}");
    }
});
```

### OpenTelemetry / Prometheus

The meter name is `"StormSocket"`. Just add an exporter — no polling loops, no custom bridge code:

```csharp
// ASP.NET Core + OpenTelemetry
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter("StormSocket")
        .AddPrometheusExporter());
```

### dotnet-counters (CLI monitoring)

Monitor live from the terminal — no code changes needed:

```bash
dotnet-counters monitor --counters StormSocket -p <pid>
```

### Available instruments

| Instrument | Type | Unit | Description |
|---|---|---|---|
| `stormsocket.connections.total` | Counter | connections | Total connections accepted |
| `stormsocket.connections.active` | UpDownCounter* | connections | Currently active connections |
| `stormsocket.messages.sent` | Counter | messages | Total messages sent |
| `stormsocket.messages.received` | Counter | messages | Total messages received |
| `stormsocket.bytes.sent` | Counter | bytes | Total bytes sent |
| `stormsocket.bytes.received` | Counter | bytes | Total bytes received |
| `stormsocket.errors` | Counter | errors | Total errors |
| `stormsocket.connection.duration` | Histogram | ms | Connection lifetime |
| `stormsocket.handshake.duration` | Histogram | ms | TLS + WS upgrade time |

\* `UpDownCounter` requires net7.0+. On net6.0, use `server.Metrics.ActiveConnections` property instead.

## Unix Domain Socket Transport

Unix domain sockets bypass the TCP/IP stack entirely — faster for local IPC (container-to-container, sidecar proxy, microservice communication on the same host).

Just pass a `UnixDomainSocketEndPoint` instead of `IPEndPoint`. Everything else works the same — events, middleware, metrics, groups, framing.

```csharp
using System.Net.Sockets;

// Server
var server = new StormTcpServer(new ServerOptions
{
    EndPoint = new UnixDomainSocketEndPoint("/tmp/myapp.sock"),
});

server.OnDataReceived += async (session, data) =>
{
    await session.SendAsync(data); // echo
};

await server.StartAsync();

// Client
var client = new StormTcpClient(new ClientOptions
{
    EndPoint = new UnixDomainSocketEndPoint("/tmp/myapp.sock"),
});

client.OnDataReceived += async data => Console.WriteLine($"Got {data.Length} bytes");
await client.ConnectAsync();
```

Works with WebSocket servers too:

```csharp
var ws = new StormWebSocketServer(new ServerOptions
{
    EndPoint = new UnixDomainSocketEndPoint("/tmp/myws.sock"),
    WebSocket = new WebSocketOptions { Heartbeat = new() { PingInterval = TimeSpan.FromSeconds(15) } },
});
```

**Notes:**
- TCP-specific socket options (`NoDelay`, `KeepAlive`) are automatically skipped for Unix sockets
- Stale `.sock` files from previous runs are automatically cleaned up on `StartAsync`
- `session.RemoteEndPoint` returns the Unix socket path instead of an IP address

## Logging

StormSocket supports structured logging via `Microsoft.Extensions.Logging`. Pass an `ILoggerFactory` through options — if omitted, logging is completely disabled with zero overhead.

```csharp
using Microsoft.Extensions.Logging;

var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Debug);
});

var server = new StormWebSocketServer(new ServerOptions
{
    EndPoint = new IPEndPoint(IPAddress.Any, 8080),
    LoggerFactory = loggerFactory,
});
```

**Log levels:**

| Level | What gets logged |
|---|---|
| `Information` | Server start/stop, client connect/disconnect |
| `Warning` | Heartbeat timeout, protocol errors, rate limit exceeded, max reconnect attempts |
| `Error` | Transport errors, handshake failures |
| `Debug` | Session connect/disconnect with details, reconnect attempts, max connections rejected |

Works on both servers and clients:

```csharp
var client = new StormWebSocketClient(new WsClientOptions
{
    Uri = new Uri("ws://localhost:8080"),
    LoggerFactory = loggerFactory,
});
```

The `RateLimitMiddleware` also accepts an optional `ILogger`:

```csharp
var rateLimiter = new RateLimitMiddleware(
    new RateLimitOptions { MaxMessages = 100 },
    logger: loggerFactory.CreateLogger<RateLimitMiddleware>());
```
