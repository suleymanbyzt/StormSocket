# API Reference

## INetworkSession (base)

Base interface shared by all session types (TCP, WebSocket, future UDP):

```csharp
public interface INetworkSession
{
    long Id { get; }                            // Unique auto-incrementing ID
    EndPoint? RemoteEndPoint { get; }           // Remote client's IP and port
    IReadOnlySet<string> Groups { get; }        // Group memberships

    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);
    void JoinGroup(string group);
    void LeaveGroup(string group);

    IDictionary<string, object?> Items { get; } // Per-session user data
    T? Get<T>(SessionKey<T> key);               // Typed accessor
    void Set<T>(SessionKey<T> key, T value);    // Typed setter
}
```

## ISession

Connection-oriented session (TCP, WebSocket). Extends `INetworkSession` with lifecycle members:

```csharp
public interface ISession : INetworkSession, IAsyncDisposable
{
    ConnectionState State { get; }              // Connected, Closing, Closed
    DisconnectReason DisconnectReason { get; }  // Why the connection was closed
    ConnectionMetrics Metrics { get; }          // BytesSent, BytesReceived, Uptime
    bool IsBackpressured { get; }               // True when send buffer is full

    ValueTask CloseAsync(CancellationToken ct = default);
    void Abort();
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
server.Sessions.All;                                       // IEnumerable<INetworkSession>
server.Sessions.TryGet(id, out INetworkSession? session);  // Lookup by ID
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
client.OnDisconnected += async (DisconnectReason reason) => { };
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
ws.OnDisconnected += async (DisconnectReason reason) => { };
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
    ValueTask OnDisconnectedAsync(ISession session, DisconnectReason reason);  // Called in reverse order
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
