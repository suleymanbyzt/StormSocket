using System.Net;
using StormSocket.Core;
using StormSocket.Events;
using StormSocket.Server;
using StormSocket.Session;
using StormSocket.WebSocket;

namespace StormSocket.Benchmark.Soak;

/// <summary>
/// The echo server the soak workload drives. Every connection is registered in two groups so the
/// group bookkeeping is unwound on every teardown, and clients can ask to be closed or aborted by
/// the server to cover the teardown paths a client-initiated close never reaches.
/// </summary>
internal sealed class SoakServer : IAsyncDisposable
{
    /// <summary>Text message that makes the server start the closing handshake for the sender.</summary>
    public const string CloseCommand = "ctl:close";

    /// <summary>Text message that makes the server abort the sender without a closing handshake.</summary>
    public const string AbortCommand = "ctl:abort";

    /// <summary>Number of shard groups sessions are spread across, alongside the "all" group.</summary>
    private const int ShardCount = 16;

    /// <summary>Longest payload inspected for a control command; anything longer is echo traffic.</summary>
    private const int MaxCommandLength = 16;

    private readonly StormWebSocketServer _server;
    private long _errorCount;

    public SoakServer()
    {
        _server = new StormWebSocketServer(new ServerOptions
        {
            // Port 0 lets the OS pick a free port, so a soak run never collides with whatever else
            // the runner has bound.
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            Backlog = 512,
            Socket = new SocketTuningOptions
            {
                NoDelay = true,
            },
            WebSocket = new WebSocketOptions
            {
                MaxMessageSize = 4 * 1024 * 1024,
                // Heartbeat pings would add timers whose lifetime is not what this run measures, and
                // the workload keeps every connection busy enough that no peer looks dead.
                Heartbeat = new HeartbeatOptions
                {
                    PingInterval = TimeSpan.Zero,
                    AutoPong = true,
                },
                // Offered, not forced: only the clients that ask for permessage-deflate negotiate it,
                // which puts both the compressed and the uncompressed path under load in one run.
                Compression = new WsCompressionOptions
                {
                    Enabled = true,
                },
                // An aborted client never answers the closing handshake; a short budget keeps those
                // sessions from holding the drain open for the default five seconds each.
                CloseTimeout = TimeSpan.FromSeconds(2),
            },
        });

        _server.OnConnected += OnConnectedAsync;
        _server.OnMessageReceived += OnMessageReceivedAsync;
        _server.OnError += OnErrorAsync;
    }

    /// <summary>The port the listener actually bound to.</summary>
    public int Port => _server.LocalEndPoint is IPEndPoint endPoint
        ? endPoint.Port
        : throw new InvalidOperationException("The server has not been started.");

    /// <summary>Connections the server still counts as live.</summary>
    public long ActiveConnections => _server.Metrics.ActiveConnections;

    /// <summary>Connections accepted since start.</summary>
    public long TotalConnections => _server.Metrics.TotalConnections;

    /// <summary>Messages the server decoded, counted on its side of the wire.</summary>
    public long MessagesReceived => _server.Metrics.MessagesReceived;

    /// <summary>Sessions still registered in the session manager.</summary>
    public int SessionCount => _server.Sessions.Count;

    /// <summary>Groups that still have at least one member (empty groups are removed by the library).</summary>
    public int GroupCount => _server.Groups.GroupNames.Count();

    /// <summary>Errors surfaced through OnError, mostly the resets the abort workload produces.</summary>
    public long ErrorCount => Interlocked.Read(ref _errorCount);

    public Task StartAsync() => _server.StartAsync();

    public Task StopAsync() => _server.StopAsync();

    private ValueTask OnConnectedAsync(IWebSocketSession session)
    {
        // Two groups per session: a leak in the group index shows up as groups that never empty
        // after every client is gone, which is exactly the regression this run guards.
        _server.Groups.Add("all", session);
        _server.Groups.Add($"shard-{session.Id % ShardCount}", session);
        return ValueTask.CompletedTask;
    }

    private async ValueTask OnMessageReceivedAsync(IWebSocketSession session, WsMessage message)
    {
        if (message.IsText && message.Data.Length <= MaxCommandLength)
        {
            string text = message.Text;

            if (text is CloseCommand)
            {
                CloseFromServer(session);
                return;
            }

            if (text is AbortCommand)
            {
                session.Abort();
                return;
            }
        }

        if (message.IsText)
        {
            await session.SendTextAsync(message.Data).ConfigureAwait(false);
        }
        else
        {
            await session.SendAsync(message.Data).ConfigureAwait(false);
        }
    }

    private void CloseFromServer(IWebSocketSession session)
    {
        // Deliberately not awaited: the closing handshake waits for the client's Close frame, and
        // that frame can only be read once this handler has returned to the connection's read loop.
        _ = Task.Run(async () =>
        {
            try
            {
                await session.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _errorCount);
            }
        });
    }

    private ValueTask OnErrorAsync(ISession? session, Exception exception)
    {
        Interlocked.Increment(ref _errorCount);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();
}
