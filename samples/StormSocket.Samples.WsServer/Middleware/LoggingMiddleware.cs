using StormSocket.Middleware;
using StormSocket.Session;

namespace StormSocket.Samples.WsServer.Middleware;

public sealed class LoggingMiddleware : IConnectionMiddleware
{
    public ValueTask OnConnectedAsync(ISession session)
    {
        Console.WriteLine($"  [MW] #{session.Id} connected");
        return ValueTask.CompletedTask;
    }

    public ValueTask OnDisconnectedAsync(ISession session)
    {
        Console.WriteLine($"  [MW] #{session.Id} disconnected  up={session.Metrics.Uptime:hh\\:mm\\:ss}  tx={session.Metrics.BytesSent}B  rx={session.Metrics.BytesReceived}B");
        return ValueTask.CompletedTask;
    }

    public ValueTask OnErrorAsync(ISession session, Exception ex)
    {
        Console.WriteLine($"  [MW] #{session.Id} error: {ex.Message}");
        return ValueTask.CompletedTask;
    }
}