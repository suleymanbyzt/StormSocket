using System.Text;
using System.Text.Json;
using StormSocket.Core;
using StormSocket.Server;
using StormSocket.Session;

namespace StormSocket.Samples.WsServer.Services;

/// <summary>
/// Helper methods for sending JSON messages to individual sessions or groups.
/// </summary>
public sealed class BroadcastHelper
{
    private readonly StormWebSocketServer _server;

    public BroadcastHelper(StormWebSocketServer server)
    {
        _server = server;
    }

    public async ValueTask SendAsync(ISession session, object payload)
    {
        if (session is WebSocketSession ws && session.State == ConnectionState.Connected)
        {
            string json = JsonSerializer.Serialize(payload);
            await ws.SendTextAsync(json);
        }
    }

    public async ValueTask BroadcastAllAsync(object payload, long? excludeId = null)
    {
        string json = JsonSerializer.Serialize(payload);
        await _server.BroadcastTextAsync(json, excludeId);
    }

    public async ValueTask BroadcastToRoomAsync(string room, object payload, long? excludeId = null)
    {
        string json = JsonSerializer.Serialize(payload);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        await _server.Groups.BroadcastAsync(room, bytes, excludeId);
    }

    public async ValueTask SystemMessageAsync(string message)
    {
        await BroadcastAllAsync(new { type = "system", message });
    }
}