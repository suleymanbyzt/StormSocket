using System.Net;
using StormSocket.Client;
using StormSocket.Server;
using StormSocket.Session;
using Xunit;

namespace StormSocket.Tests;

/// <summary>
/// Teardown paths that can run at the same time. These are the races that only show up under load or
/// on a slower machine, so they are driven in a loop rather than once.
/// </summary>
[Collection(SequentialCollection.Name)]
public class SessionTeardownRaceTests
{
    [Fact]
    public async Task CloseAsync_RacingWithConnectionTeardown_DoesNotThrow()
    {
        // A server-initiated close waits for the peer's Close frame (RFC 6455 7.1.4), and while it
        // waits the peer can vanish — the read loop ends, the handler runs its finally and disposes
        // the transport. The close then comes back to a transport that is already gone. This used to
        // surface as ObjectDisposedException from the cancellation token source.
        await using StormWebSocketServer server = new(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            WebSocket = new WebSocketOptions
            {
                Heartbeat = new() { PingInterval = TimeSpan.Zero },
                CloseTimeout = TimeSpan.FromMilliseconds(200),
            },
        });

        await server.StartAsync();
        int port = ((IPEndPoint)server.LocalEndPoint!).Port;

        for (int i = 0; i < 25; i++)
        {
            StormWebSocketClient client = new(new WsClientOptions
            {
                Uri = new Uri($"ws://127.0.0.1:{port}"),
                Heartbeat = new() { PingInterval = TimeSpan.Zero },
            });

            await client.ConnectAsync();

            WebSocketSession session = await WaitForSessionAsync(server);

            // Both directions of teardown start at once.
            Task serverClose = Task.Run(async () => await session.CloseAsync());
            Task clientGone = Task.Run(async () => await client.DisposeAsync());

            await Task.WhenAll(serverClose, clientGone).WaitAsync(TimeSpan.FromSeconds(30));

            await WaitForNoSessionsAsync(server);
        }

        Assert.Equal(0, server.Sessions.Count);
    }

    [Fact]
    public async Task AbortRacingWithDispose_DoesNotThrow()
    {
        // Abort closes the transport on a detached task; disposing the session immediately afterwards
        // must not retire the transport underneath that task.
        await using StormWebSocketServer server = new(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            WebSocket = new WebSocketOptions { Heartbeat = new() { PingInterval = TimeSpan.Zero } },
        });

        await server.StartAsync();
        int port = ((IPEndPoint)server.LocalEndPoint!).Port;

        for (int i = 0; i < 25; i++)
        {
            await using StormWebSocketClient client = new(new WsClientOptions
            {
                Uri = new Uri($"ws://127.0.0.1:{port}"),
                Heartbeat = new() { PingInterval = TimeSpan.Zero },
            });

            await client.ConnectAsync();

            WebSocketSession session = await WaitForSessionAsync(server);

            session.Abort();
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

            await WaitForNoSessionsAsync(server);
        }
    }

    private static async Task<WebSocketSession> WaitForSessionAsync(StormWebSocketServer server)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            foreach (ISession session in server.Sessions.All)
            {
                if (session is WebSocketSession ws)
                {
                    return ws;
                }
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("no session was established");
    }

    private static async Task WaitForNoSessionsAsync(StormWebSocketServer server)
    {
        for (int attempt = 0; attempt < 200 && server.Sessions.Count > 0; attempt++)
        {
            await Task.Delay(10);
        }
    }
}
