using System.Net;
using StormSocket.Core;
using StormSocket.Server;

StormWebSocketServer ws = new(new ServerOptions
{
    EndPoint = new IPEndPoint(IPAddress.Any, 8080),
    Backlog = 128,
    ReceiveBufferSize = 1024 * 64,
    SendBufferSize = 1024 * 64,
    Socket = new SocketTuningOptions
    {
        NoDelay = false,
        KeepAlive = false,
        MaxPendingReceiveBytes = 1024 * 1024,
        MaxPendingSendBytes = 1024 * 1024,
    },
    Ssl = null,
    WebSocket = new WebSocketOptions
    {
        Heartbeat = new HeartbeatOptions
        {
            PingInterval = TimeSpan.FromSeconds(1),
            MaxMissedPongs = 3,
            AutoPong = true,
        },
        MaxFrameSize = 64 * 1024,
        AllowedOrigins = null, // allow all origins
    },
    SlowConsumerPolicy = SlowConsumerPolicy.Wait,
    DualMode = true,
    MaxConnections = 10, // set to 0 for unlimited connections
});

ws.OnConnecting += async context =>
{
    IReadOnlyDictionary<string, string> headers = context.Headers;
    foreach (KeyValuePair<string, string> header in headers)
    {
        Console.WriteLine($"Header Key:{header.Key} ---  Header Value:{header.Value}");
    }

    // if you want to authenticate or reject certain connections, do it here and call context.Accept() or context.Reject()
    Console.WriteLine($"[{context.RemoteEndPoint}] WebSocket connecting...");
    context.Accept(); // accept all connections (you can add custom validation here)
    await ValueTask.CompletedTask;
};

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
