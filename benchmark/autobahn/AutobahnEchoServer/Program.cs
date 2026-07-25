using System.Net;
using StormSocket.Server;
using StormSocket.WebSocket;

// Echo server for the Autobahn Testsuite fuzzing client. It has to be a plain echo with no
// application behaviour of its own: every case in the suite judges what the library does with
// malformed or hostile frames, so anything this file adds would be measured as part of the library.
int port = args.Length > 0 ? int.Parse(args[0]) : 9001;

await using StormWebSocketServer server = new(new ServerOptions
{
    EndPoint = new IPEndPoint(IPAddress.Any, port),
    WebSocket = new WebSocketOptions
    {
        // The suite sends 16 MB payloads in section 9 and expects them to be echoed, not rejected.
        MaxFrameSize = 20 * 1024 * 1024,
        MaxMessageSize = 20 * 1024 * 1024,

        // Autobahn drives the close handshake itself and expects the server not to ping on its own.
        Heartbeat = new() { PingInterval = TimeSpan.Zero },
        Compression = new() { Enabled = true },
    },
});

server.OnMessageReceived += async (session, message) =>
{
    if (message.IsText)
    {
        await session.SendTextAsync(message.Data);
    }
    else
    {
        await session.SendAsync(message.Data);
    }
};

await server.StartAsync();
Console.WriteLine($"Autobahn echo server listening on port {port}");

// Runs until the workflow kills it.
await Task.Delay(Timeout.Infinite);
