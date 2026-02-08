using System.Net;
using StormSocket.Core;
using StormSocket.Middleware.RateLimiting;
using StormSocket.Server;
using StormSocket.Samples.WsServer.Handlers;
using StormSocket.Samples.WsServer.Middleware;
using StormSocket.Samples.WsServer.Services;

StormWebSocketServer server = new(new ServerOptions
{
    EndPoint = new IPEndPoint(IPAddress.Any, 8080),
    Backlog = 128,
    NoDelay = false,
    ReceiveBufferSize = 1024 * 64,
    SendBufferSize = 1024 * 64,
    MaxPendingReceiveBytes = 1024 * 1024,
    MaxPendingSendBytes = 1024 * 1024,
    Ssl = null,
    WebSocket = new WebSocketOptions
    {
        PingInterval = TimeSpan.FromSeconds(1),
        MaxMissedPongs = 3,
        MaxFrameSize = 64 * 1024,
        AutoPong = true,
    },
    SlowConsumerPolicy = SlowConsumerPolicy.Drop,
    DualMode = true,
    MaxConnections = 10, // set to 0 for unlimited connections
    KeepAlive = false
});

UserManager users = new();
BroadcastHelper broadcast = new(server);
TickerService ticker = new(server, users, interval: TimeSpan.FromSeconds(1));

// Rate limiting: max 50 messages per 5 seconds per IP
RateLimitMiddleware rateLimiter = new(new RateLimitOptions
{
    Window = TimeSpan.FromSeconds(5),
    MaxMessages = 5,
    Scope = RateLimitScope.Session,
    ExceededAction = RateLimitAction.Disconnect,
});

server.UseMiddleware(rateLimiter);
server.UseMiddleware(new LoggingMiddleware());

MessageHandler handler = new(server, users, broadcast, rateLimiter);
handler.Register();

await server.StartAsync();
ticker.Start();

Console.WriteLine("StormSocket WsServer running on ws://0.0.0.0:8080");
Console.WriteLine("Heartbeat: 1s tick to all clients");
Console.WriteLine("/sessions  /kick <id>  /broadcast <msg>");
Console.WriteLine("/rooms     /info <id>  /stop");

AdminConsole admin = new(server, users, broadcast);
await admin.RunAsync();

Console.WriteLine("  Shutting down...");
await ticker.DisposeAsync();
await server.DisposeAsync();
Console.WriteLine("  Done.");