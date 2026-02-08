using System.Net;
using StormSocket.Server;

StormWebSocketServer ws = new StormWebSocketServer(new ServerOptions
{
    EndPoint = new IPEndPoint(IPAddress.Any, 8080),
    WebSocket = new WebSocketOptions
    {
        PingInterval = TimeSpan.FromSeconds(30),
    }
});

ws.OnConnected += async session =>
{
    Console.WriteLine($"[{session.Id}] WebSocket connected ({ws.Sessions.Count} online)");
    await ValueTask.CompletedTask;
};

ws.OnDisconnected += async session =>
{
    Console.WriteLine($"[{session.Id}] WebSocket disconnected ({ws.Sessions.Count} online)");
    await ValueTask.CompletedTask;
};

ws.OnMessageReceived += async (session, msg) =>
{
    if (msg.IsText)
    {
        Console.WriteLine($"[{session.Id}] {msg.Text}");
        await ws.BroadcastTextAsync(msg.Text, excludeId: session.Id);
    }
};

ws.OnError += async (session, ex) =>
{
    Console.WriteLine($"[{session?.Id}] Error: {ex.Message}");
    await ValueTask.CompletedTask;
};

await ws.StartAsync();
Console.WriteLine("WebSocket Chat server listening on port 8080. Press Enter to stop.");
Console.WriteLine("Connect with: wscat -c ws://localhost:8080");
Console.ReadLine();