using System.Net;
using System.Net.Sockets;
using StormSocket.Core;
using StormSocket.Server;
using StormSocket.Session;
using Xunit;

namespace StormSocket.Tests;

public class TcpServerIntegrationTests
{
    [Fact]
    public async Task Echo_RoundTrip()
    {
        int port = GetFreePort();
        StormTcpServer server = new StormTcpServer(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            Socket = new StormSocket.Core.SocketTuningOptions { NoDelay = true },
        });

        server.OnDataReceived += async (session, data) =>
        {
            await session.SendAsync(data);
        };

        await server.StartAsync();

        try
        {
            using TcpClient client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            NetworkStream stream = client.GetStream();

            byte[] sendData = "hello"u8.ToArray();
            await stream.WriteAsync(sendData);
            await stream.FlushAsync();

            byte[] buffer = new byte[1024];
            int read = await stream.ReadAsync(buffer);

            Assert.Equal(sendData.Length, read);
            Assert.Equal(sendData, buffer[..read]);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task Connect_Disconnect_Events_Fire()
    {
        int port = GetFreePort();
        StormTcpServer server = new StormTcpServer(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
        });

        TaskCompletionSource<long> connected = new TaskCompletionSource<long>();
        TaskCompletionSource<long> disconnected = new TaskCompletionSource<long>();

        server.OnConnected += async session =>
        {
            connected.TrySetResult(session.Id);
            await ValueTask.CompletedTask;
        };

        server.OnDisconnected += async session =>
        {
            disconnected.TrySetResult(session.Id);
            await ValueTask.CompletedTask;
        };

        await server.StartAsync();

        try
        {
            using TcpClient client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);

            long connId = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(connId > 0);

            client.Close();

            long discId = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(connId, discId);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task MaxConnections_RejectsExcessClients()
    {
        int port = GetFreePort();
        StormTcpServer server = new StormTcpServer(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            MaxConnections = 1,
        });

        TaskCompletionSource connected = new TaskCompletionSource();
        server.OnConnected += async _ =>
        {
            connected.TrySetResult();
            await ValueTask.CompletedTask;
        };

        await server.StartAsync();

        try
        {
            // First client should connect
            using TcpClient client1 = new TcpClient();
            await client1.ConnectAsync(IPAddress.Loopback, port);
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, server.Sessions.Count);

            // Second client connects at TCP level but server closes it immediately
            using TcpClient client2 = new TcpClient();
            await client2.ConnectAsync(IPAddress.Loopback, port);
            await Task.Delay(200);

            Assert.Equal(1, server.Sessions.Count);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task DropPolicy_SkipsSlowClient()
    {
        int port = GetFreePort();

        StormTcpServer server = new StormTcpServer(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            Socket = new StormSocket.Core.SocketTuningOptions { MaxPendingSendBytes = 1024 },
            SlowConsumerPolicy = SlowConsumerPolicy.Drop,
        });

        TaskCompletionSource<ISession> connected = new();
        server.OnConnected += async session => connected.TrySetResult(session);
        await server.StartAsync();

        try
        {
            // Slow client  connects but NEVER reads
            using TcpClient slowClient = new TcpClient();
            slowClient.ReceiveBufferSize = 1024; // small OS buffer to fill faster
            await slowClient.ConnectAsync(IPAddress.Loopback, port);
            ISession slowSession = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Fire-and-forget: flood data until pipe fills up and IsBackpressured = true
            byte[] chunk = new byte[4096];
            using CancellationTokenSource floodCts = new();
            _ = Task.Run(async () =>
            {
                while (!floodCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await slowSession.SendAsync(chunk, floodCts.Token);
                    }
                    catch { break; }
                }
            });

            // Wait for backpressure to kick in
            bool backpressured = false;
            for (int i = 0; i < 200; i++)
            {
                await Task.Delay(25);
                if (slowSession.IsBackpressured)
                {
                    backpressured = true;
                    break;
                }
            }

            Assert.True(backpressured, "Slow client should be backpressured");

            // With Drop policy, session stays connected but sends are skipped
            Assert.Equal(StormSocket.Core.ConnectionState.Connected, slowSession.State);

            floodCts.Cancel();
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task DisconnectPolicy_ClosesSlowClient()
    {
        int port = GetFreePort();

        StormTcpServer server = new StormTcpServer(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            Socket = new StormSocket.Core.SocketTuningOptions { MaxPendingSendBytes = 1024 * 16 },
            SendBufferSize = 1024,
            SlowConsumerPolicy = SlowConsumerPolicy.Disconnect,
        });

        TaskCompletionSource<ISession> connected = new();
        TaskCompletionSource disconnected = new();
        server.OnConnected += async session => connected.TrySetResult(session);
        server.OnDisconnected += async _ => disconnected.TrySetResult();
        await server.StartAsync();

        try
        {
            // Slow client connects but NEVER reads
            using TcpClient slowClient = new TcpClient();
            slowClient.ReceiveBufferSize = 1024;
            await slowClient.ConnectAsync(IPAddress.Loopback, port);
            ISession slowSession = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Fire-and-forget: flood data until backpressure triggers Disconnect
            byte[] chunk = new byte[1024 * 32];
            using CancellationTokenSource floodCts = new();
            Task floodTask = Task.Run(async () =>
            {
                while (!floodCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await slowSession.SendAsync(chunk, floodCts.Token);
                    }
                    catch
                    {
                        break;
                    }
                }
            });

            // Wait for server to disconnect the slow client
            await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, server.Sessions.Count);

            floodCts.Cancel();
        }
        finally
        {
            await server.StopAsync();
        }
    }

    private static int GetFreePort()
    {
        using TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        
        listener.Stop();
        
        return port;
    }
}