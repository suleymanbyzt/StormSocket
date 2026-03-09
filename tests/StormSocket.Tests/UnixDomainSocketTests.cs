using System.Net;
using System.Net.Sockets;
using StormSocket.Client;
using StormSocket.Core;
using StormSocket.Server;
using StormSocket.Session;
using Xunit;

namespace StormSocket.Tests;

public class UnixDomainSocketTests : IDisposable
{
    private readonly string _socketPath;

    public UnixDomainSocketTests()
    {
        _socketPath = Path.Combine(Path.GetTempPath(), $"stormsocket_test_{Guid.NewGuid():N}.sock");
    }

    public void Dispose()
    {
        if (File.Exists(_socketPath))
        {
            File.Delete(_socketPath);
        }
    }

    [Fact]
    public async Task TcpServer_Echo_OverUnixSocket()
    {
        var server = new StormTcpServer(new ServerOptions
        {
            EndPoint = new UnixDomainSocketEndPoint(_socketPath),
        });

        server.OnDataReceived += async (session, data) =>
        {
            await session.SendAsync(data);
        };

        await server.StartAsync();

        try
        {
            using Socket client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath));

            byte[] sendData = "hello unix"u8.ToArray();
            await client.SendAsync(sendData);

            byte[] buffer = new byte[1024];
            int read = await client.ReceiveAsync(buffer);

            Assert.Equal(sendData.Length, read);
            Assert.Equal(sendData, buffer[..read]);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task TcpServer_ConnectDisconnect_Events_OverUnixSocket()
    {
        var server = new StormTcpServer(new ServerOptions
        {
            EndPoint = new UnixDomainSocketEndPoint(_socketPath),
        });

        TaskCompletionSource<long> connected = new();
        TaskCompletionSource<DisconnectReason> disconnected = new();

        server.OnConnected += async session =>
        {
            connected.TrySetResult(session.Id);
        };

        server.OnDisconnected += async (session, reason) =>
        {
            disconnected.TrySetResult(reason);
        };

        await server.StartAsync();

        try
        {
            using Socket client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath));

            long connId = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(connId > 0);

            client.Shutdown(SocketShutdown.Both);
            client.Close();

            DisconnectReason reason = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(DisconnectReason.ClosedByClient, reason);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task TcpClient_Echo_OverUnixSocket()
    {
        var server = new StormTcpServer(new ServerOptions
        {
            EndPoint = new UnixDomainSocketEndPoint(_socketPath),
        });

        server.OnDataReceived += async (session, data) =>
        {
            await session.SendAsync(data);
        };

        await server.StartAsync();

        try
        {
            TaskCompletionSource<byte[]> received = new();

            var client = new StormTcpClient(new ClientOptions
            {
                EndPoint = new UnixDomainSocketEndPoint(_socketPath),
            });

            client.OnDataReceived += async data =>
            {
                received.TrySetResult(data.ToArray());
            };

            await client.ConnectAsync();

            byte[] sendData = "hello storm unix"u8.ToArray();
            await client.SendAsync(sendData);

            byte[] result = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(sendData, result);

            await client.DisposeAsync();
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task StaleSocketFile_IsCleanedUp()
    {
        // Create a stale socket file
        await File.WriteAllTextAsync(_socketPath, "stale");
        Assert.True(File.Exists(_socketPath));

        var server = new StormTcpServer(new ServerOptions
        {
            EndPoint = new UnixDomainSocketEndPoint(_socketPath),
        });

        // Server should delete the stale file and bind successfully
        await server.StartAsync();

        try
        {
            using Socket client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath));

            // If we get here, bind succeeded after cleaning stale file
            Assert.True(true);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task ServerMetrics_WorkOverUnixSocket()
    {
        var server = new StormTcpServer(new ServerOptions
        {
            EndPoint = new UnixDomainSocketEndPoint(_socketPath),
        });

        server.OnDataReceived += async (session, data) =>
        {
            await session.SendAsync(data);
        };

        await server.StartAsync();

        try
        {
            using Socket client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath));

            // Wait for connection to register
            await Task.Delay(100);
            Assert.Equal(1, server.Metrics.ActiveConnections);

            byte[] data = "metrics test"u8.ToArray();
            await client.SendAsync(data);

            byte[] buffer = new byte[1024];
            await client.ReceiveAsync(buffer);

            // Wait for metrics to update
            await Task.Delay(100);
            Assert.True(server.Metrics.MessagesReceived > 0);
            Assert.True(server.Metrics.MessagesSent > 0);

            client.Shutdown(SocketShutdown.Both);
            client.Close();
            await Task.Delay(200);

            Assert.Equal(0, server.Metrics.ActiveConnections);
            Assert.Equal(1, server.Metrics.TotalConnections);
        }
        finally
        {
            await server.StopAsync();
        }
    }
}
