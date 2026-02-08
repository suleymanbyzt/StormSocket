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

## Your First TCP Server

Create a simple TCP echo server that sends back everything it receives:

```csharp
using System.Net;
using StormSocket.Server;

StormTcpServer server = new StormTcpServer(new ServerOptions
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
    Console.WriteLine($"[{session.Id}] Echo {data.Length} bytes");
    await session.SendAsync(data);
};

server.OnError += async (session, ex) =>
{
    Console.WriteLine($"[{session?.Id}] Error: {ex.Message}");
};

await server.StartAsync();
Console.WriteLine("TCP Echo server listening on port 5000. Press Enter to stop.");
Console.ReadLine();
```

Test it with a TCP client (e.g. `telnet localhost 5000` or `ncat localhost 5000`).

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
        PingInterval = TimeSpan.FromSeconds(30),
    }
});

ws.OnConnected += async session =>
{
    Console.WriteLine($"[{session.Id}] WebSocket connected ({ws.Sessions.Count} online)");
    await ValueTask.CompletedTask;
};

ws.OnDisconnected += async session =>
{
    Console.WriteLine($"[{session.Id}] WebSocket disconnected ({ws.Sessions.Count} online)");
    await ValueTask.CompletedTask;
};

ws.OnMessageReceived += async (session, msg) =>
{
    if (msg.IsText)
    {
        Console.WriteLine($"[{session.Id}] {msg.Text}");
        await ws.BroadcastTextAsync(msg.Text, excludeId: session.Id);
    }
};

ws.OnError += async (session, ex) =>
{
    Console.WriteLine($"[{session?.Id}] Error: {ex.Message}");
    await ValueTask.CompletedTask;
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
