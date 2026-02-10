using System.Net;
using StormSocket.Core;
using StormSocket.Middleware.RateLimiting;
using StormSocket.Server;

StormTcpServer server = new StormTcpServer(new ServerOptions
{
    EndPoint = new IPEndPoint(IPAddress.Any, 5000),
    Socket = new SocketTuningOptions
    {
        NoDelay = true
    },
});

// Rate limiting: max 100 messages per 10 seconds per session
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

server.OnConnected += async session =>
{
    Console.WriteLine($"[{session.Id}] Connected ({server.Sessions.Count} online)");
};

server.OnDisconnected += async session =>
{
    Console.WriteLine($"[{session.Id}] Disconnected (sent={session.Metrics.BytesSent}, recv={session.Metrics.BytesReceived})");
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