using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using StormSocket.Server;
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
            NoDelay = true,
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

    private static int GetFreePort()
    {
        using TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        
        listener.Stop();
        
        return port;
    }
}