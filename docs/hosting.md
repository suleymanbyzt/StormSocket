# Dependency Injection & Hosting

`StormSocket.Extensions.Hosting` runs a StormSocket server as part of a .NET Generic Host: created
from the container, started and stopped with the application, drained on shutdown, and reported to
health checks. The core `StormSocket` package stays dependency-free; install this one only if you
want the integration.

```bash
dotnet add package StormSocket.Extensions.Hosting
```

## The shortest version

```csharp
builder.Services
    .AddStormWebSocketServer()
    .ListenOnAnyIP(8080)
    .AddHandler<ChatHandler>();
```

```csharp
public sealed class ChatHandler : IWebSocketHandler
{
    public async ValueTask OnMessageAsync(IWebSocketSession session, WsMessage message, CancellationToken cancellationToken)
        => await session.SendTextAsync(message.Text, cancellationToken);
}
```

That is the whole integration: the server starts with the host, stops with it, and logs through the
application's logger factory without any extra wiring.

## Handlers

`IWebSocketHandler` has three members and default implementations for two of them, so implement only
what you need:

```csharp
ValueTask OnConnectedAsync(IWebSocketSession session, CancellationToken cancellationToken);
ValueTask OnMessageAsync(IWebSocketSession session, WsMessage message, CancellationToken cancellationToken);
ValueTask OnDisconnectedAsync(IWebSocketSession session, DisconnectReason reason, CancellationToken cancellationToken);
```

`ITcpConnectionHandler` is the TCP counterpart, with `OnDataReceivedAsync` in place of `OnMessageAsync`.

Register as many handlers as you like; they run in registration order:

```csharp
builder.Services
    .AddStormWebSocketServer()
    .AddHandler<AuditHandler>()
    .AddHandler<ChatHandler>();
```

A handler that throws is logged and the next handler still runs. The connection stays up — a bug in
one message should not disconnect the user.

### Lifetimes

Handlers are **scoped by default**: each invocation gets its own DI scope, so injecting a `DbContext`
or any other scoped service behaves exactly as it does in a web request.

```csharp
public sealed class ChatHandler(AppDbContext db) : IWebSocketHandler   // scoped dependency, safe
```

That scope costs an allocation per message. On a hot path where the handler has no scoped
dependencies, register it as a singleton and the scope disappears:

```csharp
.AddHandler<TickerHandler>(ServiceLifetime.Singleton)
```

A singleton handler is resolved once and must be safe to use concurrently — messages from different
connections arrive in parallel.

> `message.Data` points into a buffer the connection reuses for the next frame. It is valid for the
> duration of the handler; copy it (`message.Data.ToArray()`) if it outlives the call. `message.Text`
> already returns an independent string.

## Configuration

The configuration delegate receives the same `ServerOptions` the standalone API uses, so everything
in the [configuration reference](configuration.md) is available:

```csharp
builder.Services.AddStormWebSocketServer(options =>
{
    options.MaxConnections = 10_000;
    options.MaxConnectionsPerIp = 20;
    options.SlowConsumerPolicy = SlowConsumerPolicy.Drop;
    options.WebSocket!.IdleTimeout = TimeSpan.FromMinutes(5);
    options.WebSocket.Compression.Enabled = true;
});
```

Several calls compose in registration order, which is what lets a library ship defaults that the
application then overrides:

```csharp
builder.Services.AddStormWebSocketServer(o => o.MaxConnections = 1_000);   // library default
builder.Services.AddStormWebSocketServer(o => o.MaxConnections = 50_000);  // application wins
```

### From appsettings.json

```csharp
builder.Services
    .AddStormWebSocketServer()
    .BindConfiguration(builder.Configuration.GetSection("StormSocket"));
```

```json
{
  "StormSocket": {
    "Host": "any",
    "Port": 8080,
    "MaxConnections": 10000,
    "MaxConnectionsPerIp": 20,
    "WebSocket": {
      "MaxFrameSize": 1048576,
      "IdleTimeout": "00:05:00",
      "Heartbeat": { "PingInterval": "00:00:20" }
    }
  }
}
```

Everything binds by name, nested sections included. The endpoint is the exception: `EndPoint` is an
abstract type configuration cannot construct, so it is spelled as `Host` and `Port`. `Host` accepts
an IP address, `any`, `*` or `localhost`, and a value that is neither throws at startup naming the
key that is wrong.

Binding composes with the delegate, so configuration can supply the deployment-specific values while
code sets what should never differ:

```csharp
builder.Services
    .AddStormWebSocketServer(options => options.SlowConsumerPolicy = SlowConsumerPolicy.Drop)
    .BindConfiguration(builder.Configuration.GetSection("StormSocket"));
```

### From other services

Some options are not known until the container exists — a certificate from a secret store, an
endpoint from service discovery. The provider-aware overload runs in the same order as the rest:

```csharp
builder.Services
    .AddStormWebSocketServer()
    .Configure((options, services) =>
    {
        options.Ssl = new SslOptions { Certificate = services.GetRequiredService<ICertificateStore>().Current };
    });
```

## Middleware

```csharp
builder.Services
    .AddStormWebSocketServer()
    .UseMiddleware<AuthMiddleware>();
```

Middleware is resolved once per server, so it must be safe to use concurrently. When it needs
constructor arguments the container cannot supply, register the instance yourself:

```csharp
builder.Services.AddSingleton<IConnectionMiddleware>(new RateLimitMiddleware(new RateLimitOptions
{
    Window = TimeSpan.FromSeconds(10),
    MaxMessages = 100,
}));
```

## Health checks

```csharp
builder.Services.AddHealthChecks().AddStormWebSocketServer();
app.MapHealthChecks("/health");
```

Reports `Healthy` while the server is listening, with the active connection count in the result data,
and the configured failure status when it is not. Use it as a readiness probe: a pod whose socket
server failed to bind should not receive traffic.

## Shutdown

`StopAsync` stops accepting connections, closes sessions with `GoingAway`, and then waits for
in-flight handlers to finish before tearing anything down. The wait is bounded by
`ServerOptions.ShutdownDrainTimeout` (10 seconds by default) and by the host's own shutdown token,
whichever comes first — so a pod terminating on a 30-second grace period finishes the work it can and
never hangs past it.

```csharp
builder.Services.AddStormWebSocketServer(options => options.ShutdownDrainTimeout = TimeSpan.FromSeconds(20));
builder.Host.ConfigureHostOptions(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));
```

Handlers receive the application's stopping token, so long-running work can react to shutdown instead
of being abandoned:

```csharp
public async ValueTask OnMessageAsync(IWebSocketSession session, WsMessage message, CancellationToken cancellationToken)
{
    await _queue.PublishAsync(message.Text, cancellationToken);   // cancelled when the host stops
}
```

## Alongside ASP.NET Core

The server owns its own port; it does not share Kestrel's. That is deliberate — the WebSocket
handshake, framing and backpressure are handled by StormSocket end to end, so there is no HTTP
pipeline in front of it. Kestrel keeps serving HTTP on its port, both start and stop with the host,
and health checks cover both.

See `samples/StormSocket.Samples.AspNetCore` for a full application: minimal API endpoints, a chat
handler with a scoped dependency, singleton application state and a mapped health endpoint.

```bash
dotnet run --project samples/StormSocket.Samples.AspNetCore
# HTTP:      http://localhost:5000/health
# WebSocket: ws://localhost:8080
```

## Getting at the server directly

The server itself is a singleton in the container, so anything that needs to broadcast can take it as
a dependency:

```csharp
app.MapPost("/announce", async (string text, StormWebSocketServer server) =>
{
    await server.BroadcastTextAsync(text);
    return Results.Accepted();
});
```

## Limits

One WebSocket server and one TCP server per container. Repeated `AddStormWebSocketServer` calls add
configuration to the same server rather than creating a second one, which keeps
`GetRequiredService<StormWebSocketServer>()` unambiguous. To listen on two ports, run two hosts.
