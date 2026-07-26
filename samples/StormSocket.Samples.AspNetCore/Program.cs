using System.Collections.Concurrent;
using StormSocket.Core;
using StormSocket.Events;
using StormSocket.Extensions.Hosting;
using StormSocket.Session;

// StormSocket next to ASP.NET Core in one host: Kestrel serves HTTP on its own port while the
// WebSocket server owns another, and both start, stop and report health together.
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ChatRoom>();
builder.Services.AddScoped<MessageLog>();

builder.Services
    .AddStormWebSocketServer(options =>
    {
        options.MaxConnections = 10_000;
        options.WebSocket!.IdleTimeout = TimeSpan.FromMinutes(5);
        options.WebSocket.Heartbeat.PingInterval = TimeSpan.FromSeconds(20);
    })
    .ListenOnAnyIP(8080)
    .AddHandler<ChatHandler>();

builder.Services.AddHealthChecks().AddStormWebSocketServer();

WebApplication app = builder.Build();

app.MapGet("/", () => "HTTP is served by Kestrel on this port; WebSocket clients connect to ws://localhost:8080");
app.MapGet("/rooms", (ChatRoom room) => new { room.MemberCount });
app.MapHealthChecks("/health");

app.Run();

/// <summary>Handles WebSocket traffic. Resolved from DI per message, so scoped services work here.</summary>
internal sealed class ChatHandler(ChatRoom room, MessageLog log, ILogger<ChatHandler> logger) : IWebSocketHandler
{
    public ValueTask OnConnectedAsync(IWebSocketSession session, CancellationToken cancellationToken)
    {
        room.Join(session);
        logger.LogInformation("#{SessionId} joined from {RemoteEndPoint}", session.Id, session.RemoteEndPoint);
        return session.SendTextAsync("welcome", cancellationToken);
    }

    public async ValueTask OnMessageAsync(IWebSocketSession session, WsMessage message, CancellationToken cancellationToken)
    {
        if (!message.IsText)
        {
            return;
        }

        // MessageLog is scoped: a fresh instance per message, exactly like a per-request dependency.
        log.Record(message.Text);

        await room.BroadcastAsync($"#{session.Id}: {message.Text}", exceptSessionId: session.Id, cancellationToken);
    }

    public ValueTask OnDisconnectedAsync(IWebSocketSession session, DisconnectReason reason, CancellationToken cancellationToken)
    {
        room.Leave(session);
        logger.LogInformation("#{SessionId} left ({Reason})", session.Id, reason);
        return default;
    }
}

/// <summary>Application state shared across connections — a singleton, like any other service.</summary>
internal sealed class ChatRoom
{
    private readonly ConcurrentDictionary<long, IWebSocketSession> _members = new();

    public int MemberCount => _members.Count;

    public void Join(IWebSocketSession session) => _members[session.Id] = session;

    public void Leave(IWebSocketSession session) => _members.TryRemove(session.Id, out _);

    public async ValueTask BroadcastAsync(string text, long exceptSessionId, CancellationToken cancellationToken)
    {
        foreach (IWebSocketSession member in _members.Values)
        {
            if (member.Id == exceptSessionId)
            {
                continue;
            }

            await member.SendTextAsync(text, cancellationToken);
        }
    }
}

/// <summary>Stands in for a scoped dependency such as a DbContext.</summary>
internal sealed class MessageLog(ILogger<MessageLog> logger)
{
    public void Record(string text) => logger.LogDebug("message: {Text}", text);
}
