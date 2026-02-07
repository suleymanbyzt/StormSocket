using StormSocket.Server;
using StormSocket.Session;
using StormSocket.Samples.WsServer.Models;
using StormSocket.Samples.WsServer.Services;

namespace StormSocket.Samples.WsServer.Handlers;

/// <summary>
/// Interactive admin console for managing the server from stdin.
/// </summary>
public sealed class AdminConsole
{
    private readonly StormWebSocketServer _server;
    private readonly UserManager _users;
    private readonly BroadcastHelper _broadcast;

    public AdminConsole(StormWebSocketServer server, UserManager users, BroadcastHelper broadcast)
    {
        _server = server;
        _users = users;
        _broadcast = broadcast;
    }

    /// <summary>
    /// Blocks on stdin. Returns when the admin types /stop or stdin closes.
    /// </summary>
    public async Task RunAsync()
    {
        while (true)
        {
            string? line = Console.ReadLine();
            if (line is null) break;
            line = line.Trim();
            if (line.Length == 0) continue;

            string[] parts = line.Split(' ', 2);
            string cmd = parts[0].ToLowerInvariant();
            string arg = parts.Length > 1 ? parts[1] : "";

            switch (cmd)
            {
                case "/sessions": ListSessions(); break;
                case "/kick":     await KickSession(arg); break;
                case "/broadcast": await Broadcast(arg); break;
                case "/rooms":    ListRooms(); break;
                case "/info":     ShowSessionInfo(arg); break;
                case "/stop":     return;
                default:
                    Log("Commands: /sessions  /kick <id>  /broadcast <msg>  /rooms  /info <id>  /stop");
                    break;
            }
        }
    }

    private void ListSessions()
    {
        Log($"┌── Sessions ({_server.Sessions.Count}) ──");
        foreach (ISession s in _server.Sessions.All)
        {
            ConnectedUser? user = _users.Get(s.Id);
            string name = user?.Name ?? "?";
            string groups = string.Join(", ", s.Groups);
            Log($"│ #{s.Id,-4} {name,-14} up={s.Metrics.Uptime:hh\\:mm\\:ss}  groups=[{groups}]  tx={s.Metrics.BytesSent}B  rx={s.Metrics.BytesReceived}B");
        }
        Log($"└── {_server.Sessions.Count} total");
    }

    private async Task KickSession(string arg)
    {
        if (!long.TryParse(arg.Trim(), out long id))
        {
            Log("Usage: /kick <id>");
            return;
        }

        if (!_server.Sessions.TryGet(id, out ISession? session) || session is null)
        {
            Log($"#{id} not found.");
            return;
        }

        if (session is WebSocketSession ws)
        {
            await ws.SendTextAsync("{\"type\":\"system\",\"message\":\"Kicked by admin.\"}");
        }

        await session.CloseAsync();
        Log($"#{id} kicked.");
    }

    private async Task Broadcast(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            Log("Usage: /broadcast <message>");
            return;
        }

        await _broadcast.SystemMessageAsync($"[ADMIN] {message}");
        Log($"Sent to {_server.Sessions.Count} clients.");
    }

    private void ListRooms()
    {
        Log("┌── Rooms ──");
        foreach (string name in _server.Groups.GroupNames)
        {
            Log($"│ {name,-18} {_server.Groups.MemberCount(name)} members");
        }
        Log("└──");
    }

    private void ShowSessionInfo(string arg)
    {
        if (!long.TryParse(arg.Trim(), out long id))
        {
            Log("Usage: /info <id>");
            return;
        }

        if (!_server.Sessions.TryGet(id, out ISession? session) || session is null)
        {
            Log($"#{id} not found.");
            return;
        }

        ConnectedUser? user = _users.Get(id);

        Log($"ID: #{session.Id}");
        Log($"Name: {user?.Name ?? "?"}");
        Log($"State: {session.State}");
        Log($"Uptime: {session.Metrics.Uptime:hh\\:mm\\:ss}");
        Log($"Sent: {session.Metrics.BytesSent:N0} B");
        Log($"Received: {session.Metrics.BytesReceived:N0} B");
        Log($"Groups: [{string.Join(", ", session.Groups)}]");
    }

    private static void Log(string msg) => Console.WriteLine($"  {msg}");
}