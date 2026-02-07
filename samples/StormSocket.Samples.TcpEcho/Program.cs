using System.Net;
using System.Threading.Tasks;
using StormSocket.Server;

StormTcpServer server = new StormTcpServer(new ServerOptions
{
    EndPoint = new IPEndPoint(IPAddress.Any, 5000),
    NoDelay = true,
    WebSocket = new WebSocketOptions()
    {
        AutoPong = true,
        PingInterval = TimeSpan.FromSeconds(15),
        MaxFrameSize = 1024 * 1024, // 1 MB
        MaxMissedPongs = 2
    }
});

server.OnConnected += async session =>
{
    Console.WriteLine($"[{session.Id}] Connected");
    await ValueTask.CompletedTask;
};

server.OnDisconnected += async session =>
{
    Console.WriteLine($"[{session.Id}] Disconnected (sent={session.Metrics.BytesSent}, recv={session.Metrics.BytesReceived})");
    await ValueTask.CompletedTask;
};

server.OnDataReceived += async (session, data) =>
{
    Console.WriteLine($"[{session.Id}] Echo {data.Length} bytes");
    await session.SendAsync(data);
};

server.OnError += async (session, ex) =>
{
    Console.WriteLine($"[{session?.Id}] Error: {ex.Message}");
    await ValueTask.CompletedTask;
};

await server.StartAsync();
Console.WriteLine("TCP Echo server listening on port 5000. Press Enter to stop.");
Console.ReadLine();