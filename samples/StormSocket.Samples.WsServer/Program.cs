using System.IO.Compression;
using System.Net;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using StormSocket.Core;
using StormSocket.Middleware.RateLimiting;
using StormSocket.Server;
using StormSocket.WebSocket;
using StormSocket.Samples.WsServer.Handlers;
using StormSocket.Samples.WsServer.Middleware;
using StormSocket.Samples.WsServer.Services;

using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Debug);
});

StormWebSocketServer server = new(new ServerOptions
{
    EndPoint = new IPEndPoint(IPAddress.Any, 8080),
    LoggerFactory = loggerFactory,
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
        MaxMessageSize = 1024 * 1,
        MaxFrameSize = 64 * 1024,
        AllowedOrigins = null, // allow all origins
        Compression = new WsCompressionOptions
        {
            Enabled = true,
            CompressionLevel = CompressionLevel.Optimal,
            // others are defaults: MinMessageSize=128, ServerNoContextTakeover=true, ClientNoContextTakeover=true, ServerMaxWindowBits=15, ClientMaxWindowBits=15
        },
    },
    SlowConsumerPolicy = SlowConsumerPolicy.Drop,
    DualMode = true,
    MaxConnections = 10, // set to 0 for unlimited connections
});

UserManager users = new();
BroadcastHelper broadcast = new(server);
TickerService ticker = new(server, users, interval: TimeSpan.FromSeconds(1));

// Rate limiting: max 100 messages per 5 seconds per IP
RateLimitMiddleware rateLimiter = new(new RateLimitOptions
{
    Window = TimeSpan.FromSeconds(5),
    MaxMessages = 5,
    Scope = RateLimitScope.Session,
    ExceededAction = RateLimitAction.Disconnect,
}, loggerFactory.CreateLogger<RateLimitMiddleware>());

server.UseMiddleware(rateLimiter);
server.UseMiddleware(new LoggingMiddleware());

MessageHandler handler = new(server, users, broadcast, rateLimiter);
handler.Register();

await server.StartAsync();
ticker.Start();

// OpenTelemetry Prometheus metrics endpoint: http://localhost:9464/metrics
// Browse to see all StormSocket counters, histograms, and gauges in Prometheus format.
using MeterProvider meterProvider = Sdk.CreateMeterProviderBuilder()
    .AddMeter("StormSocket")
    .AddPrometheusHttpListener(o => o.UriPrefixes = ["http://localhost:9464/"])
    .Build()!;

Console.WriteLine("StormSocket WsServer running on ws://0.0.0.0:8080");
Console.WriteLine("Prometheus metrics:  http://localhost:9464/metrics");
Console.WriteLine("Heartbeat: 1s tick to all clients");
Console.WriteLine("/sessions  /kick <id>  /broadcast <msg>  /metrics");
Console.WriteLine("/rooms     /info <id>  /stop");

AdminConsole admin = new(server, users, broadcast);
await admin.RunAsync();

Console.WriteLine("  Shutting down...");
await ticker.DisposeAsync();
await server.DisposeAsync();
Console.WriteLine("  Done.");
