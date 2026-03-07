using StormSocket.Middleware;
using StormSocket.Session;

namespace StormSocket.Samples.WsServer.Middleware;

public sealed class LoggingMiddleware : IConnectionMiddleware
{
    public ValueTask OnConnectedAsync(ISession networkSession)
    {
        Console.WriteLine($"  [MW] #{networkSession.Id} connected");
        return ValueTask.CompletedTask;
    }

    public ValueTask OnDisconnectedAsync(ISession networkSession)
    {
        Console.WriteLine($"  [MW] #{networkSession.Id} disconnected  up={networkSession.Metrics.Uptime:hh\\:mm\\:ss}  tx={networkSession.Metrics.BytesSent}B  rx={networkSession.Metrics.BytesReceived}B");
        return ValueTask.CompletedTask;
    }

    public ValueTask OnErrorAsync(ISession networkSession, Exception ex)
    {
        Console.WriteLine($"  [MW] #{networkSession.Id} error: {ex.Message}");
        return ValueTask.CompletedTask;
    }
}