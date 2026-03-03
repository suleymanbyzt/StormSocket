# Examples

## TCP Echo Server

The simplest possible server. Echoes back everything it receives.

```csharp
using System.Net;
using StormSocket.Server;

var server = new StormTcpServer(new ServerOptions
{
    EndPoint = new IPEndPoint(IPAddress.Any, 5000),
    Socket = new SocketTuningOptions { NoDelay = true },
});

server.OnConnected += async session =>
{
    Console.WriteLine($"[{session.Id}] Connected ({server.Sessions.Count} online)");
};

server.OnDisconnected += async (session, reason) =>
{
    Console.WriteLine($"[{session.Id}] Disconnected ({reason})");
};

server.OnDataReceived += async (session, data) =>
{
    Console.WriteLine($"[{session.Id}] {data.Length} bytes received");
    await session.SendAsync(data);
};

server.OnError += async (session, ex) =>
{
    Console.WriteLine($"[{session?.Id}] Error: {ex.Message}");
};

await server.StartAsync();
Console.WriteLine("TCP Echo listening on :5000");
Console.ReadLine();
await server.DisposeAsync();
```

Test with: `telnet localhost 5000`

## WebSocket Chat Server

Broadcasts every message to all connected WebSocket clients.

```csharp
using System.Net;
using StormSocket.Server;

var ws = new StormWebSocketServer(new ServerOptions
{
    EndPoint = new IPEndPoint(IPAddress.Any, 8080),
    WebSocket = new WebSocketOptions
    {
        Heartbeat = new HeartbeatOptions
        {
            PingInterval = TimeSpan.FromSeconds(15),
            MaxMissedPongs = 3,
        },
    },
});

ws.OnConnected += async session =>
{
    Console.WriteLine($"[{session.Id}] WebSocket connected");
};

ws.OnMessageReceived += async (session, msg) =>
{
    if (msg.IsText)
    {
        Console.WriteLine($"[{session.Id}] {msg.Text}");
        // Broadcast to everyone except sender
        await ws.BroadcastTextAsync(msg.Text, excludeId: session.Id);
    }
};

await ws.StartAsync();
Console.WriteLine("WebSocket Chat listening on :8080");
Console.ReadLine();
await ws.DisposeAsync();
```

Test with: `wscat -c ws://localhost:8080`

## WebSocket Authentication

Authenticate clients before accepting WebSocket connections using `OnConnecting`. Access headers, cookies, query params, and path from the HTTP upgrade request.

```csharp
using System.Net;
using StormSocket.Server;

var ws = new StormWebSocketServer(new ServerOptions
{
    EndPoint = new IPEndPoint(IPAddress.Any, 8080),
});

ws.OnConnecting += async (context) =>
{
    // access request details
    Console.WriteLine($"Path: {context.Path}");                    // "/chat"
    Console.WriteLine($"Query: {context.Query["room"]}");          // "general"
    Console.WriteLine($"Remote: {context.RemoteEndPoint}");        // "192.168.1.5:54321"

    // check authorization header
    string? token = context.Headers.GetValueOrDefault("Authorization");
    if (string.IsNullOrEmpty(token) || !IsValidToken(token))
    {
        context.Reject(401, "Invalid or missing token");
        return;
    }

    // check origin for browser clients
    // or you can use allowedorigins in options
    string? origin = context.Headers.GetValueOrDefault("Origin");
    if (origin != "https://myapp.com")
    {
        context.Reject(403, "Origin not allowed");
        return;
    }

    context.Accept();
};

ws.OnConnected += async session =>
{
    Console.WriteLine($"Authenticated client connected: #{session.Id}");
};

await ws.StartAsync();
```

If no `OnConnecting` handler is registered, all connections are auto-accepted (backwards compatible).

## SSL/TLS Server

Any server can be upgraded to SSL by adding `SslOptions`. No separate class needed.

```csharp
using System.Net;
using System.Security.Cryptography.X509Certificates;
using StormSocket.Server;

var server = new StormTcpServer(new ServerOptions
{
    EndPoint = new IPEndPoint(IPAddress.Any, 5001),
    Ssl = new SslOptions
    {
        Certificate = X509CertificateLoader.LoadPkcs12FromFile("server.pfx", "password"),
    },
});

server.OnDataReceived += async (session, data) => await session.SendAsync(data);

await server.StartAsync();
```

Works the same with `StormWebSocketServer` for WSS (WebSocket Secure).

## TCP Client

Connect to a TCP server with auto-reconnect, framing, and middleware support.

```csharp
using System.Net;
using System.Text;
using StormSocket.Client;

var client = new StormTcpClient(new ClientOptions
{
    EndPoint = new IPEndPoint(IPAddress.Loopback, 5000),
    Socket = new SocketTuningOptions { NoDelay = true },
    Reconnect = new ReconnectOptions { Enabled = true, Delay = TimeSpan.FromSeconds(2) },
});

client.OnConnected += async () =>
{
    Console.WriteLine("Connected to server!");
};

client.OnDataReceived += async data =>
{
    Console.WriteLine($"Received: {Encoding.UTF8.GetString(data.Span)}");
};

client.OnDisconnected += async (reason) =>
{
    Console.WriteLine($"Disconnected from server ({reason})");
};

client.OnReconnecting += async (attempt, delay) =>
{
    Console.WriteLine($"Reconnecting (attempt #{attempt})...");
};

await client.ConnectAsync();
await client.SendAsync(Encoding.UTF8.GetBytes("Hello Server!"));
```

Use the same `IMessageFramer` on both server and client for message boundaries:

```csharp
var framer = new LengthPrefixFramer();

// Server
var server = new StormTcpServer(new ServerOptions { Framer = framer });

// Client
var client = new StormTcpClient(new ClientOptions { Framer = framer });
```

## WebSocket Client

Connect to any WebSocket server with automatic masking, heartbeat, and reconnect.

```csharp
using StormSocket.Client;

var ws = new StormWebSocketClient(new WsClientOptions
{
    Uri = new Uri("ws://localhost:8080/chat"),
    Reconnect = new ReconnectOptions { Enabled = true },
    Heartbeat = new HeartbeatOptions { PingInterval = TimeSpan.FromSeconds(15) },
});

ws.OnConnected += async () =>
{
    Console.WriteLine("WebSocket connected!");
    await ws.SendTextAsync("Hello from StormSocket!");
};

ws.OnMessageReceived += async msg =>
{
    if (msg.IsText)
        Console.WriteLine($"Server says: {msg.Text}");
    else
        Console.WriteLine($"Binary data: {msg.Data.Length} bytes");
};

ws.OnDisconnected += async (reason) =>
{
    Console.WriteLine($"WebSocket disconnected ({reason})");
};

await ws.ConnectAsync();
```

For WSS (WebSocket Secure), use the `wss://` scheme:

```csharp
var ws = new StormWebSocketClient(new WsClientOptions
{
    Uri = new Uri("wss://echo.websocket.org"),
});
```

Send custom HTTP headers during the upgrade handshake:

```csharp
var ws = new StormWebSocketClient(new WsClientOptions
{
    Uri = new Uri("ws://localhost:8080"),
    Headers = new Dictionary<string, string>
    {
        { "Authorization", "Bearer my-token" },
    },
});
```

## Full WebSocket Server with Admin Console

See `samples/StormSocket.Samples.WsServer` for a complete example featuring:

- JSON command protocol (setName, chat, whisper, join/leave rooms, list users, etc.)
- Per-second heartbeat tick with timestamp broadcast to all clients
- Admin console with `/sessions`, `/kick`, `/broadcast`, `/rooms`, `/info` commands
- Middleware-based connection logging
- Session management with user names and room membership

```bash
dotnet run --project samples/StormSocket.Samples.WsServer
```

```
StormSocket WsServer running on ws://0.0.0.0:8080
/sessions  /kick <id>  /broadcast <msg>
/rooms     /info <id>  /stop
```
