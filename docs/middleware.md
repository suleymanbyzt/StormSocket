# Middleware

StormSocket provides a middleware pipeline that intercepts connection lifecycle events and data flow. Middleware is executed in registration order for incoming data and in reverse order for disconnection.

## The Pipeline

Register middleware on any server instance:

```csharp
server.UseMiddleware(new LoggingMiddleware());
server.UseMiddleware(new RateLimitMiddleware(options));
```

Middleware runs in order:
1. **OnConnectedAsync** — called after a session is established (registration order)
2. **OnDataReceivedAsync** — called before `OnDataReceived` fires (registration order)
3. **OnDataSendingAsync** — called before data is sent to the client (registration order)
4. **OnDisconnectedAsync** — called after disconnect (reverse order)
5. **OnErrorAsync** — called when an exception occurs (registration order)

## IConnectionMiddleware Interface

```csharp
public interface IConnectionMiddleware
{
    ValueTask OnConnectedAsync(ISession session);

    ValueTask<ReadOnlyMemory<byte>> OnDataReceivedAsync(
        ISession session, ReadOnlyMemory<byte> data);

    ValueTask<ReadOnlyMemory<byte>> OnDataSendingAsync(
        ISession session, ReadOnlyMemory<byte> data);

    ValueTask OnDisconnectedAsync(ISession session);

    ValueTask OnErrorAsync(ISession session, Exception exception);
}
```

All methods have default no-op implementations, so you only need to override the ones you care about.

## Writing a Custom Middleware

### Logging Middleware

```csharp
using StormSocket.Middleware;
using StormSocket.Session;

public sealed class LoggingMiddleware : IConnectionMiddleware
{
    public ValueTask OnConnectedAsync(ISession session)
    {
        Console.WriteLine($"[LOG] #{session.Id} connected");
        return ValueTask.CompletedTask;
    }

    public ValueTask OnDisconnectedAsync(ISession session)
    {
        Console.WriteLine(
            $"[LOG] #{session.Id} disconnected  " +
            $"up={session.Metrics.Uptime:hh\\:mm\\:ss}  " +
            $"tx={session.Metrics.BytesSent}B  " +
            $"rx={session.Metrics.BytesReceived}B");
        return ValueTask.CompletedTask;
    }

    public ValueTask OnErrorAsync(ISession session, Exception ex)
    {
        Console.WriteLine($"[LOG] #{session.Id} error: {ex.Message}");
        return ValueTask.CompletedTask;
    }
}
```

### Data-Transforming Middleware

Middleware can modify or suppress data flowing through the pipeline. Return `ReadOnlyMemory<byte>.Empty` from `OnDataReceivedAsync` to drop a message:

```csharp
public sealed class ProfanityFilter : IConnectionMiddleware
{
    public ValueTask<ReadOnlyMemory<byte>> OnDataReceivedAsync(
        ISession session, ReadOnlyMemory<byte> data)
    {
        string text = Encoding.UTF8.GetString(data.Span);

        if (ContainsProfanity(text))
        {
            // Drop the message — it won't reach OnDataReceived
            return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
        }

        return ValueTask.FromResult(data);
    }

    private static bool ContainsProfanity(string text) => false; // your logic
}
```

## Built-in: Rate Limiting

StormSocket ships with a rate-limiting middleware out of the box.

### Configuration

```csharp
using StormSocket.Middleware.RateLimiting;

var rateLimiter = new RateLimitMiddleware(new RateLimitOptions
{
    Window = TimeSpan.FromSeconds(10),  // Time window
    MaxMessages = 100,                  // Max messages per window
    Scope = RateLimitScope.Session,     // Per-session or per-IP
    ExceededAction = RateLimitAction.Disconnect, // Disconnect or Drop
});
```

### Options

| Property | Default | Description |
|---|---|---|
| `Window` | 1 second | Time window for counting messages |
| `MaxMessages` | 100 | Max allowed messages per window |
| `Scope` | `Session` | `Session` or `IpAddress` |
| `ExceededAction` | `Disconnect` | `Disconnect` the client or `Drop` the message |

### Events

```csharp
rateLimiter.OnExceeded += async session =>
{
    Console.WriteLine($"[{session.Id}] Rate limit exceeded");
};
```

### Scope: Per-IP

Use `RateLimitScope.IpAddress` to share a single counter across all connections from the same IP address. This is useful for preventing a single IP from opening many connections to bypass per-session limits:

```csharp
var rateLimiter = new RateLimitMiddleware(new RateLimitOptions
{
    Window = TimeSpan.FromSeconds(1),
    MaxMessages = 50,
    Scope = RateLimitScope.IpAddress,
    ExceededAction = RateLimitAction.Disconnect,
});
```

### Full Example

```csharp
using System.Net;
using StormSocket.Middleware.RateLimiting;
using StormSocket.Server;

var server = new StormTcpServer(new ServerOptions
{
    EndPoint = new IPEndPoint(IPAddress.Any, 5000),
});

var rateLimiter = new RateLimitMiddleware(new RateLimitOptions
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

server.OnDataReceived += async (session, data) =>
{
    await session.SendAsync(data);
};

await server.StartAsync();
```
