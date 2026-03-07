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
                case "/metrics":  ShowMetrics(); break;
                case "/stop":     return;
                default:
                    Log("Commands: /sessions  /kick <id>  /broadcast <msg>  /rooms  /info <id>  /metrics  /stop");
                    break;
            }
        }
    }

    private void ListSessions()
    {
        Log($"┌── Sessions ({_server.Sessions.Count}) ──");
        foreach (INetworkSession s in _server.Sessions.All)
        {
            ConnectedUser? user = _users.Get(s.Id);
            string name = user?.Name ?? "?";
            string groups = string.Join(", ", s.Groups);
            if (s is ISession cs)
            {
                Log($"│ #{s.Id,-4} {name,-14} up={cs.Metrics.Uptime:hh\\:mm\\:ss}  groups=[{groups}]  tx={cs.Metrics.BytesSent}B  rx={cs.Metrics.BytesReceived}B");
            }
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

        if (!_server.Sessions.TryGet(id, out INetworkSession? networkSession) || networkSession is null)
        {
            Log($"#{id} not found.");
            return;
        }

        if (networkSession is WebSocketSession ws)
        {
            await ws.SendTextAsync("{\"type\":\"system\",\"message\":\"Kicked by admin.\"}");
        }

        if (networkSession is ISession connSession)
        {
            await connSession.CloseAsync();
        }
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

        if (!_server.Sessions.TryGet(id, out INetworkSession? networkSession) || networkSession is null)
        {
            Log($"#{id} not found.");
            return;
        }

        ConnectedUser? user = _users.Get(id);

        Log($"ID: #{networkSession.Id}");
        Log($"Name: {user?.Name ?? "?"}");
        if (networkSession is ISession connSession)
        {
            Log($"State: {connSession.State}");
            Log($"Uptime: {connSession.Metrics.Uptime:hh\\:mm\\:ss}");
            Log($"Sent: {connSession.Metrics.BytesSent:N0} B");
            Log($"Received: {connSession.Metrics.BytesReceived:N0} B");
        }
        Log($"Groups: [{string.Join(", ", networkSession.Groups)}]");
    }

    private void ShowMetrics()
    {
        var m = _server.Metrics;
        Log("┌── Server Metrics ──");
        Log($"│ Active connections:  {m.ActiveConnections}");
        Log($"│ Total connections:   {m.TotalConnections}");
        Log($"│ Messages sent:       {m.MessagesSent:N0}");
        Log($"│ Messages received:   {m.MessagesReceived:N0}");
        Log($"│ Bytes sent:          {m.BytesSentTotal:N0}");
        Log($"│ Bytes received:      {m.BytesReceivedTotal:N0}");
        Log($"│ Errors:              {m.ErrorCount}");
        Log("└──");
    }

    private static void Log(string msg) => Console.WriteLine($"  {msg}");
}