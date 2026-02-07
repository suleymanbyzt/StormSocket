using System.Text.Json;
using StormSocket.Events;
using StormSocket.Server;
using StormSocket.Session;
using StormSocket.Samples.WsServer.Models;
using StormSocket.Samples.WsServer.Services;

namespace StormSocket.Samples.WsServer.Handlers;

/// <summary>
/// Handles all incoming WebSocket messages from clients.
/// JSON protocol: {"type":"...", ...}
/// Plain text falls back to lobby chat.
/// </summary>
public sealed class MessageHandler
{
    private readonly StormWebSocketServer _server;
    private readonly UserManager _users;
    private readonly BroadcastHelper _broadcast;

    public MessageHandler(StormWebSocketServer server, UserManager users, BroadcastHelper broadcast)
    {
        _server = server;
        _users = users;
        _broadcast = broadcast;
    }

    public void Register()
    {
        _server.OnConnected += OnConnected;
        _server.OnDisconnected += OnDisconnected;
        _server.OnMessageReceived += OnMessage;
        _server.OnError += OnError;
    }

    private async ValueTask OnConnected(ISession session)
    {
        ConnectedUser user = _users.Add(session);
        _server.Groups.Add("lobby", session);

        await _broadcast.SendAsync(session, new
        {
            type = "welcome",
            id = session.Id,
            message = "Connected. Commands: setName, chat, whisper, join, leave, roomMsg, list, rooms, myInfo",
            online = _server.Sessions.Count,
        });

        await _broadcast.SystemMessageAsync($"#{session.Id} joined (online: {_server.Sessions.Count})");
    }

    private async ValueTask OnDisconnected(ISession session)
    {
        if (_users.Remove(session.Id, out ConnectedUser? user))
        {
            _server.Groups.RemoveFromAll(session);
            await _broadcast.SystemMessageAsync($"{user!.Name} left (online: {_server.Sessions.Count})");
        }
    }

    private async ValueTask OnMessage(ISession session, WsMessage msg)
    {
        if (!msg.IsText) return;

        ConnectedUser? user = _users.Get(session.Id);
        if (user is null) return;

        string text = msg.Text.Trim();

        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            string? type = root.TryGetProperty("type", out JsonElement t) ? t.GetString() : null;

            switch (type)
            {
                case "setName":   await OnSetName(session, user, root); break;
                case "chat":      await OnChat(session, user, root); break;
                case "whisper":   await OnWhisper(session, user, root); break;
                case "join":      await OnJoinRoom(session, user, root); break;
                case "leave":     await OnLeaveRoom(session, user, root); break;
                case "roomMsg":   await OnRoomMessage(session, user, root); break;
                case "list":      await OnListUsers(session); break;
                case "rooms":     await OnListRooms(session); break;
                case "myInfo":    await OnMyInfo(session, user); break;
                default:
                    await _broadcast.SendAsync(session, new { type = "error", message = $"Unknown: {type}" });
                    break;
            }
        }
        catch (JsonException)
        {
            await _broadcast.BroadcastToRoomAsync("lobby", new
            {
                type = "chat",
                from = user.Name,
                room = "lobby",
                message = text,
            }, excludeId: session.Id);
        }
    }

    private async ValueTask OnSetName(ISession session, ConnectedUser user, JsonElement root)
    {
        string? name = root.GetProperty("name").GetString()?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            await _broadcast.SendAsync(session, new { type = "error", message = "Name cannot be empty." });
            return;
        }

        if (_users.IsNameTaken(name, excludeId: session.Id))
        {
            await _broadcast.SendAsync(session, new { type = "error", message = $"'{name}' is taken." });
            return;
        }

        string old = user.Name;
        user.Name = name;

        await _broadcast.SendAsync(session, new { type = "nameSet", name });
        await _broadcast.SystemMessageAsync($"{old} is now known as {name}");
    }

    private async ValueTask OnChat(ISession session, ConnectedUser user, JsonElement root)
    {
        string? message = root.GetProperty("message").GetString();
        if (string.IsNullOrWhiteSpace(message)) return;

        await _broadcast.BroadcastToRoomAsync("lobby", new
        {
            type = "chat",
            from = user.Name,
            room = "lobby",
            message,
        }, excludeId: session.Id);
    }

    private async ValueTask OnWhisper(ISession session, ConnectedUser user, JsonElement root)
    {
        long targetId = root.GetProperty("to").GetInt64();
        string? message = root.GetProperty("message").GetString();
        if (string.IsNullOrWhiteSpace(message)) return;

        ConnectedUser? target = _users.Get(targetId);
        if (target is null)
        {
            await _broadcast.SendAsync(session, new { type = "error", message = $"#{targetId} not found." });
            return;
        }

        await _broadcast.SendAsync(target.Session, new
        {
            type = "whisper",
            from = user.Name,
            fromId = session.Id,
            message,
        });
        await _broadcast.SendAsync(session, new { type = "whisperSent", to = targetId, message });
    }

    private async ValueTask OnJoinRoom(ISession session, ConnectedUser user, JsonElement root)
    {
        string? room = root.GetProperty("room").GetString()?.Trim();
        if (string.IsNullOrEmpty(room)) return;

        _server.Groups.Add(room, session);

        await _broadcast.SendAsync(session, new
        {
            type = "joined",
            room,
            members = _server.Groups.MemberCount(room),
        });
        await _broadcast.BroadcastToRoomAsync(room, new
        {
            type = "system",
            message = $"{user.Name} joined '{room}'",
        }, excludeId: session.Id);
    }

    private async ValueTask OnLeaveRoom(ISession session, ConnectedUser user, JsonElement root)
    {
        string? room = root.GetProperty("room").GetString()?.Trim();
        if (string.IsNullOrEmpty(room) || room == "lobby") return;

        _server.Groups.Remove(room, session);

        await _broadcast.SendAsync(session, new { type = "left", room });
        await _broadcast.BroadcastToRoomAsync(room, new
        {
            type = "system",
            message = $"{user.Name} left '{room}'",
        });
    }

    private async ValueTask OnRoomMessage(ISession session, ConnectedUser user, JsonElement root)
    {
        string? room = root.GetProperty("room").GetString();
        string? message = root.GetProperty("message").GetString();
        if (string.IsNullOrWhiteSpace(room) || string.IsNullOrWhiteSpace(message)) return;

        if (!session.Groups.Contains(room))
        {
            await _broadcast.SendAsync(session, new { type = "error", message = $"Not in room '{room}'." });
            return;
        }

        await _broadcast.BroadcastToRoomAsync(room, new
        {
            type = "chat",
            from = user.Name,
            room,
            message,
        }, excludeId: session.Id);
    }

    private async ValueTask OnListUsers(ISession session)
    {
        var list = _users.All.Select(u => new
        {
            id = u.Id,
            name = u.Name,
            uptime = u.Session.Metrics.Uptime.ToString(@"hh\:mm\:ss"),
            groups = u.Session.Groups.ToArray(),
        });

        await _broadcast.SendAsync(session, new { type = "userList", users = list, total = _server.Sessions.Count });
    }

    private async ValueTask OnListRooms(ISession session)
    {
        var list = _server.Groups.GroupNames.Select(r => new
        {
            room = r,
            members = _server.Groups.MemberCount(r),
        });

        await _broadcast.SendAsync(session, new { type = "roomList", rooms = list });
    }

    private async ValueTask OnMyInfo(ISession session, ConnectedUser user)
    {
        await _broadcast.SendAsync(session, new
        {
            type = "myInfo",
            id = session.Id,
            name = user.Name,
            state = session.State.ToString(),
            uptime = session.Metrics.Uptime.ToString(@"hh\:mm\:ss"),
            bytesSent = session.Metrics.BytesSent,
            bytesReceived = session.Metrics.BytesReceived,
            groups = session.Groups.ToArray(),
        });
    }

    private async ValueTask OnError(ISession? session, Exception ex)
    {
        Console.WriteLine($"  [ERROR] #{session?.Id}: {ex.Message}");
        await ValueTask.CompletedTask;
    }
}