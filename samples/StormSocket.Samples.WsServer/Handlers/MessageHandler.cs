using System.Text.Json;
using StormSocket.Core;
using StormSocket.Events;
using StormSocket.Middleware.RateLimiting;
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
    private static readonly SessionKey<ConnectedUser> UserKey = new("user");

    private readonly StormWebSocketServer _server;
    private readonly UserManager _users;
    private readonly BroadcastHelper _broadcast;

    public MessageHandler(StormWebSocketServer server, UserManager users, BroadcastHelper broadcast, RateLimitMiddleware? rateLimiter = null)
    {
        _server = server;
        _users = users;
        _broadcast = broadcast;

        if (rateLimiter is not null)
        {
            rateLimiter.OnExceeded += OnRateLimitExceeded;
        }
    }

    public void Register()
    {
        _server.OnConnected += OnConnected;
        _server.OnDisconnected += OnDisconnected;
        _server.OnMessageReceived += OnMessage;
        _server.OnError += OnError;
    }

    private async ValueTask OnConnected(ISession networkSession)
    {
        ConnectedUser user = _users.Add(networkSession);
        networkSession.Set(UserKey, user);
        _server.Groups.Add("lobby", networkSession);

        await _broadcast.SendAsync(networkSession, new
        {
            type = "welcome",
            id = networkSession.Id,
            message = "Connected. Commands: setName, chat, whisper, join, leave, roomMsg, list, rooms, myInfo",
            online = _server.Sessions.Count,
        });

        await _broadcast.SystemMessageAsync($"#{networkSession.Id} joined (online: {_server.Sessions.Count})");
    }

    private async ValueTask OnDisconnected(ISession networkSession, DisconnectReason reason)
    {
        if (_users.Remove(networkSession.Id, out ConnectedUser? user))
        {
            _server.Groups.RemoveFromAll(networkSession);
            await _broadcast.SystemMessageAsync($"{user!.Name} left (online: {_server.Sessions.Count})");
        }
    }

    private async ValueTask OnMessage(ISession networkSession, WsMessage msg)
    {
        if (!msg.IsText) return;

        ConnectedUser? user = networkSession.Get(UserKey);
        if (user is null) return;

        string text = msg.Text.Trim();

        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            string? type = root.TryGetProperty("type", out JsonElement t) ? t.GetString() : null;

            switch (type)
            {
                case "setName":
                    await OnSetName(networkSession, user, root); break;
                
                case "chat":
                    await OnChat(networkSession, user, root); break;
                
                case "whisper":
                    await OnWhisper(networkSession, user, root); break;
                
                case "join":
                    await OnJoinRoom(networkSession, user, root); break;
                
                case "leave":
                    await OnLeaveRoom(networkSession, user, root); break;
                
                case "roomMsg":
                    await OnRoomMessage(networkSession, user, root); break;
                
                case "list":
                    await OnListUsers(networkSession); break;
                
                case "rooms":
                    await OnListRooms(networkSession); break;
                
                case "myInfo":
                    await OnMyInfo(networkSession, user); break;
                
                default:
                    await _broadcast.SendAsync(networkSession, new { type = "error", message = $"Unknown: {type}" });
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
            }, excludeId: networkSession.Id);
        }
    }

    private async ValueTask OnSetName(ISession networkSession, ConnectedUser user, JsonElement root)
    {
        string? name = root.GetProperty("name").GetString()?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            await _broadcast.SendAsync(networkSession, new { type = "error", message = "Name cannot be empty." });
            return;
        }

        if (_users.IsNameTaken(name, excludeId: networkSession.Id))
        {
            await _broadcast.SendAsync(networkSession, new { type = "error", message = $"'{name}' is taken." });
            return;
        }

        string old = user.Name;
        user.Name = name;

        await _broadcast.SendAsync(networkSession, new { type = "nameSet", name });
        await _broadcast.SystemMessageAsync($"{old} is now known as {name}");
    }

    private async ValueTask OnChat(ISession networkSession, ConnectedUser user, JsonElement root)
    {
        string? message = root.GetProperty("message").GetString();
        if (string.IsNullOrWhiteSpace(message)) return;

        await _broadcast.BroadcastToRoomAsync("lobby", new
        {
            type = "chat",
            from = user.Name,
            room = "lobby",
            message,
        }, excludeId: networkSession.Id);
    }

    private async ValueTask OnWhisper(ISession networkSession, ConnectedUser user, JsonElement root)
    {
        long targetId = root.GetProperty("to").GetInt64();
        string? message = root.GetProperty("message").GetString();
        if (string.IsNullOrWhiteSpace(message)) return;

        ConnectedUser? target = _users.Get(targetId);
        if (target is null)
        {
            await _broadcast.SendAsync(networkSession, new { type = "error", message = $"#{targetId} not found." });
            return;
        }

        await _broadcast.SendAsync(target.NetworkSession, new
        {
            type = "whisper",
            from = user.Name,
            fromId = networkSession.Id,
            message,
        });
        await _broadcast.SendAsync(networkSession, new { type = "whisperSent", to = targetId, message });
    }

    private async ValueTask OnJoinRoom(ISession networkSession, ConnectedUser user, JsonElement root)
    {
        string? room = root.GetProperty("room").GetString()?.Trim();
        if (string.IsNullOrEmpty(room)) return;

        _server.Groups.Add(room, networkSession);

        await _broadcast.SendAsync(networkSession, new
        {
            type = "joined",
            room,
            members = _server.Groups.MemberCount(room),
        });
        await _broadcast.BroadcastToRoomAsync(room, new
        {
            type = "system",
            message = $"{user.Name} joined '{room}'",
        }, excludeId: networkSession.Id);
    }

    private async ValueTask OnLeaveRoom(ISession networkSession, ConnectedUser user, JsonElement root)
    {
        string? room = root.GetProperty("room").GetString()?.Trim();
        if (string.IsNullOrEmpty(room) || room == "lobby") return;

        _server.Groups.Remove(room, networkSession);

        await _broadcast.SendAsync(networkSession, new { type = "left", room });
        await _broadcast.BroadcastToRoomAsync(room, new
        {
            type = "system",
            message = $"{user.Name} left '{room}'",
        });
    }

    private async ValueTask OnRoomMessage(ISession networkSession, ConnectedUser user, JsonElement root)
    {
        string? room = root.GetProperty("room").GetString();
        string? message = root.GetProperty("message").GetString();
        if (string.IsNullOrWhiteSpace(room) || string.IsNullOrWhiteSpace(message)) return;

        if (!networkSession.Groups.Contains(room))
        {
            await _broadcast.SendAsync(networkSession, new { type = "error", message = $"Not in room '{room}'." });
            return;
        }

        await _broadcast.BroadcastToRoomAsync(room, new
        {
            type = "chat",
            from = user.Name,
            room,
            message,
        }, excludeId: networkSession.Id);
    }

    private async ValueTask OnListUsers(ISession networkSession)
    {
        var list = _users.All.Select(u => new
        {
            id = u.Id,
            name = u.Name,
            uptime = u.NetworkSession.Metrics.Uptime.ToString(@"hh\:mm\:ss"),
            groups = u.NetworkSession.Groups.ToArray(),
        });

        await _broadcast.SendAsync(networkSession, new { type = "userList", users = list, total = _server.Sessions.Count });
    }

    private async ValueTask OnListRooms(ISession networkSession)
    {
        var list = _server.Groups.GroupNames.Select(r => new
        {
            room = r,
            members = _server.Groups.MemberCount(r),
        });

        await _broadcast.SendAsync(networkSession, new { type = "roomList", rooms = list });
    }

    private async ValueTask OnMyInfo(ISession networkSession, ConnectedUser user)
    {
        await _broadcast.SendAsync(networkSession, new
        {
            type = "myInfo",
            id = networkSession.Id,
            name = user.Name,
            state = networkSession.State.ToString(),
            uptime = networkSession.Metrics.Uptime.ToString(@"hh\:mm\:ss"),
            bytesSent = networkSession.Metrics.BytesSent,
            bytesReceived = networkSession.Metrics.BytesReceived,
            groups = networkSession.Groups.ToArray(),
        });
    }

    private async ValueTask OnRateLimitExceeded(ISession networkSession)
    {
        ConnectedUser? user = _users.Get(networkSession.Id);
        string name = user?.Name ?? $"#{networkSession.Id}";
        Console.WriteLine($"[RateLimit] {name} ({networkSession.RemoteEndPoint}) exceeded limit");
        await _broadcast.SystemMessageAsync($"{name} was disconnected (rate limit exceeded)");
    }

    private ValueTask OnError(ISession? session, Exception ex)
    {
        Console.WriteLine($"  [ERROR] #{session?.Id}: {ex.Message}");
        return ValueTask.CompletedTask;
    }
}