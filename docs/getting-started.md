# Getting Started

## Installation

Install StormSocket from NuGet:

```bash
dotnet add package StormSocket
```

Or via the Package Manager Console:

```powershell
Install-Package StormSocket
```

## Common Usings

Most projects need these namespaces:

```csharp
using StormSocket.Server;   // StormTcpServer, StormWebSocketServer, ServerOptions
using StormSocket.Client;   // StormTcpClient, StormWebSocketClient
using StormSocket.Session;  // ISession, IWebSocketSession, SessionKey<T>
using StormSocket.Core;     // DisconnectReason, SlowConsumerPolicy, ConnectionMetrics
using StormSocket.Framing;  // LengthPrefixFramer, DelimiterFramer
```

> **Tip:** For simple server-only usage, `using StormSocket.Server;` is often enough — `ServerOptions`, `WebSocketOptions` etc. are all in that namespace.

## Your First TCP Server

Create a simple TCP echo server that sends back everything it receives:

```csharp
using System.Net;
using StormSocket.Server;

StormTcpServer server = new StormTcpServer(new ServerOptions
{
    EndPoint = new IPEndPoint(IPAddress.Any, 5000),
    Socket = new StormSocket.Core.SocketTuningOptions { NoDelay = true },
});

server.OnConnected += session =>
{
    Console.WriteLine($"[{session.Id}] Connected ({server.Sessions.Count} online)");
    return ValueTask.CompletedTask;
};

server.OnDisconnected += (session, reason) =>
{
    Console.WriteLine($"[{session.Id}] Disconnected ({reason})");
    return ValueTask.CompletedTask;
};

server.OnDataReceived += async (session, data) =>
{
    Console.WriteLine($"[{session.Id}] Echo {data.Length} bytes");
    await session.SendAsync(data);
};

server.OnError += (session, ex) =>
{
    Console.WriteLine($"[{session?.Id}] Error: {ex.Message}");
    return ValueTask.CompletedTask;
};

await server.StartAsync();
Console.WriteLine("TCP Echo server listening on port 5000. Press Enter to stop.");
Console.ReadLine();
```

Test it with a TCP client (e.g. `telnet localhost 5000` or `ncat localhost 5000`).

> **Note:** Event handlers return `ValueTask`. Use `async` when you need `await`, otherwise return `ValueTask.CompletedTask` directly to avoid async state machine overhead.

## Your First WebSocket Server

Create a WebSocket chat server that broadcasts messages to all connected clients:

```csharp
using System.Net;
using StormSocket.Server;

StormWebSocketServer ws = new StormWebSocketServer(new ServerOptions
{
    EndPoint = new IPEndPoint(IPAddress.Any, 8080),
    WebSocket = new WebSocketOptions
    {
        Heartbeat = new StormSocket.Core.HeartbeatOptions
        {
            PingInterval = TimeSpan.FromSeconds(30),
        },
    }
});

ws.OnConnected += session =>
{
    Console.WriteLine($"[{session.Id}] WebSocket connected ({ws.Sessions.Count} online)");
    return ValueTask.CompletedTask;
};

ws.OnDisconnected += (session, reason) =>
{
    Console.WriteLine($"[{session.Id}] WebSocket disconnected ({reason})");
    return ValueTask.CompletedTask;
};

// session is IWebSocketSession — SendTextAsync available directly
ws.OnMessageReceived += async (session, msg) =>
{
    if (msg.IsText)
    {
        Console.WriteLine($"[{session.Id}] {msg.Text}");
        await ws.BroadcastTextAsync(msg.Text, excludeId: session.Id);
    }
};

ws.OnError += (session, ex) =>
{
    Console.WriteLine($"[{session?.Id}] Error: {ex.Message}");
    return ValueTask.CompletedTask;
};

await ws.StartAsync();
Console.WriteLine("WebSocket Chat server listening on port 8080. Press Enter to stop.");
Console.ReadLine();
```

Test it with: `wscat -c ws://localhost:8080`

## Adding Rate Limiting

Protect your server from misbehaving clients with built-in rate limiting:

```csharp
using StormSocket.Middleware.RateLimiting;

RateLimitMiddleware rateLimiter = new(new RateLimitOptions
{
    Window = TimeSpan.FromSeconds(10),
    MaxMessages = 100,
    Scope = RateLimitScope.Session,
    ExceededAction = RateLimitAction.Disconnect,
});

rateLimiter.OnExceeded += async session =>
{
    Console.WriteLine($"[{session.Id}] Rate limit exceeded — disconnecting");
};

server.UseMiddleware(rateLimiter);
```

See [Middleware](middleware.md) for more details on the middleware pipeline.
