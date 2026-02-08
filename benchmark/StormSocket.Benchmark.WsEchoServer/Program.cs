using System.Net;
using StormSocket.Server;

int port = 8080;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-p" or "--port":
            port = int.Parse(args[++i]);
            break;
    }
}

Console.WriteLine($"Server port: {port}");
Console.WriteLine();

StormWebSocketServer server = new StormWebSocketServer(new ServerOptions
{
    EndPoint = new IPEndPoint(IPAddress.Any, port),
    NoDelay = true,
    WebSocket = new WebSocketOptions
    {
        PingInterval = TimeSpan.Zero,
        AutoPong = true,
    },
});

server.OnMessageReceived += async (session, msg) =>
{
    await session.SendAsync(msg.Data);
};

server.OnError += async (session, ex) =>
{
    Console.WriteLine($"Server error: {ex.Message}");
    await ValueTask.CompletedTask;
};

await server.StartAsync();
Console.WriteLine("Server started. Press Enter to stop...");
Console.ReadLine();
await server.StopAsync();
