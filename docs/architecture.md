# Architecture & Internals

## Connection Lifecycle

**Server-side:**
```
Client connects
    |
    +- Socket.AcceptAsync
    +- TcpTransport created (Pipe pair with backpressure limits)
    +- SSL handshake (if SslOptions configured)
    +- WebSocket HTTP upgrade (if StormWebSocketServer)
    +- Session created, added to SessionManager
    +- Middleware.OnConnectedAsync
    +- OnConnected event
    |
    +- Read loop (decodes frames, reassembles fragments, dispatches events)
    +- Heartbeat loop (sends Pings, tracks Pongs) [concurrent]
    |
    +- Connection closes (client disconnect / timeout / kick)
    +- DisconnectReason set on session
    +- Middleware.OnDisconnectedAsync(session, reason) (reverse order)
    +- OnDisconnected(session, reason) event
    +- Session removed from SessionManager
    +- Transport disposed
```

**Client-side:**
```
ConnectAsync called
    |
    +- Socket.ConnectAsync (with timeout)
    +- TcpTransport or SslTransport created
    +- SSL handshake (if configured or wss://)
    +- WebSocket HTTP upgrade (if StormWebSocketClient)
    +- Middleware.OnConnectedAsync
    +- OnConnected event
    |
    +- Read/frame loop (dispatches OnDataReceived / OnMessageReceived)
    +- Heartbeat loop (WebSocket: sends masked Pings) [concurrent]
    |
    +- Connection closes (server disconnect / timeout / DisconnectAsync)
    +- DisconnectReason determined
    +- Middleware.OnDisconnectedAsync(session, reason) (reverse order)
    +- OnDisconnected(reason) event
    |
    +- [Reconnect.Enabled = true]: wait Reconnect.Delay -> OnReconnecting -> retry
    +- Transport disposed
```

## Write Serialization

All writes to a WebSocket connection are serialized through a per-session `SemaphoreSlim`:

```
session.SendTextAsync("hello")  -+
heartbeat ping                   +---> _writeLock -> PipeWriter -> Socket
auto-pong                       -+
```

This prevents frame interleaving when multiple sources write concurrently (user code, heartbeat timer, auto-pong handler).

## Backpressure

StormSocket uses `System.IO.Pipelines` with configurable `pauseWriterThreshold` and `resumeWriterThreshold`:

```
[Socket] -> ReceivePipe (1MB limit) -> [Frame Decoder] -> [Event Handler]
[Event Handler] -> [Frame Encoder] -> SendPipe (1MB limit) -> [Socket]
```

When a pipe fills up:
- **Receive pipe full**: `ReceiveAsync` pauses -> OS TCP window closes -> sender slows down
- **Send pipe full**: `session.IsBackpressured = true` -> `SlowConsumerPolicy` kicks in:
  - `Wait`: `FlushAsync` awaits -> caller waits -> no memory growth
  - `Drop`: `SendAsync` returns immediately -> message discarded -> no blocking
  - `Disconnect`: `CloseAsync` fired -> session removed -> no slow consumer

Resume happens at 50% of the threshold to avoid oscillation.

**Broadcast is concurrent**: Each session's send runs in parallel. One slow session never blocks delivery to others, regardless of policy.
