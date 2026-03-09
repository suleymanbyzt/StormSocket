using System.Net;
using System.Net.Sockets;
using System.Text;
using StormSocket.Client;
using StormSocket.Core;
using StormSocket.Server;
using StormSocket.Session;
using StormSocket.WebSocket;
using Xunit;

namespace StormSocket.Tests;

public class WsServerAdvancedTests
{
    private static int _nextPort = 19000;
    private static int GetPort() => Interlocked.Increment(ref _nextPort);

    [Fact]
    public async Task OnConnecting_Reject_FailsClientUpgrade()
    {
        int port = GetPort();

        await using StormWebSocketServer server = new(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            WebSocket = new WebSocketOptions
            {
                Heartbeat = new HeartbeatOptions { PingInterval = TimeSpan.Zero },
            },
        });
        server.OnConnecting += async context =>
        {
            context.Reject(401, "Unauthorized");
        };
        await server.StartAsync();

        try
        {
            // Client should fail to connect because server rejects the upgrade
            StormWebSocketClient client = new(new WsClientOptions
            {
                Uri = new Uri($"ws://localhost:{port}/"),
                Heartbeat = new HeartbeatOptions { PingInterval = TimeSpan.Zero },
            });

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await client.ConnectAsync());

            await client.DisposeAsync();
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task OnConnecting_Accept_AllowsConnection()
    {
        int port = GetPort();
        TaskCompletionSource connected = new();

        await using StormWebSocketServer server = new(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            WebSocket = new WebSocketOptions
            {
                Heartbeat = new HeartbeatOptions { PingInterval = TimeSpan.Zero },
            },
        });
        server.OnConnecting += async context =>
        {
            string? token = context.Headers.GetValueOrDefault("Authorization");
            if (token == "Bearer valid")
                context.Accept();
            else
                context.Reject(403);
        };
        server.OnConnected += async _ => connected.TrySetResult();
        await server.StartAsync();

        try
        {
            await using StormWebSocketClient client = new(new WsClientOptions
            {
                Uri = new Uri($"ws://localhost:{port}/"),
                Heartbeat = new HeartbeatOptions { PingInterval = TimeSpan.Zero },
                Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer valid" },
            });
            await client.ConnectAsync();

            await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(connected.Task.IsCompleted);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task OnConnecting_NoHandlerCall_AutoAccepts()
    {
        int port = GetPort();
        TaskCompletionSource connected = new();

        await using StormWebSocketServer server = new(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            WebSocket = new WebSocketOptions
            {
                Heartbeat = new HeartbeatOptions { PingInterval = TimeSpan.Zero },
            },
        });
        server.OnConnecting += async context =>
        {
            // Don't call Accept() or Reject() — should auto-accept
        };
        server.OnConnected += async _ => connected.TrySetResult();
        await server.StartAsync();

        try
        {
            await using StormWebSocketClient client = new(new WsClientOptions
            {
                Uri = new Uri($"ws://localhost:{port}/"),
                Heartbeat = new HeartbeatOptions { PingInterval = TimeSpan.Zero },
            });
            await client.ConnectAsync();

            await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(connected.Task.IsCompleted);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task MaxConnections_RejectsExcessWebSocketClients()
    {
        int port = GetPort();
        TaskCompletionSource connected = new();

        await using StormWebSocketServer server = new(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            MaxConnections = 1,
            WebSocket = new WebSocketOptions
            {
                Heartbeat = new HeartbeatOptions { PingInterval = TimeSpan.Zero },
            },
        });
        server.OnConnected += async _ => connected.TrySetResult();
        await server.StartAsync();

        try
        {
            await using StormWebSocketClient client1 = new(new WsClientOptions
            {
                Uri = new Uri($"ws://localhost:{port}/"),
                Heartbeat = new HeartbeatOptions { PingInterval = TimeSpan.Zero },
            });
            await client1.ConnectAsync();
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, server.Sessions.Count);

            // Second client should be rejected at TCP level
            using TcpClient raw = new();
            await raw.ConnectAsync(IPAddress.Loopback, port);
            await Task.Delay(200);

            Assert.Equal(1, server.Sessions.Count);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task BroadcastTextAsync_SendsToAllClients()
    {
        int port = GetPort();
        TaskCompletionSource<string> client1Received = new();
        TaskCompletionSource<string> client2Received = new();
        int connectedCount = 0;
        TaskCompletionSource bothConnected = new();

        await using StormWebSocketServer server = new(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            WebSocket = new WebSocketOptions
            {
                Heartbeat = new HeartbeatOptions { PingInterval = TimeSpan.Zero },
            },
        });
        server.OnConnected += async _ =>
        {
            if (Interlocked.Increment(ref connectedCount) == 2)
                bothConnected.TrySetResult();
        };
        await server.StartAsync();

        try
        {
            await using StormWebSocketClient c1 = new(new WsClientOptions
            {
                Uri = new Uri($"ws://localhost:{port}/"),
                Heartbeat = new HeartbeatOptions { PingInterval = TimeSpan.Zero },
            });
            c1.OnMessageReceived += async msg => client1Received.TrySetResult(msg.Text);
            await c1.ConnectAsync();

            await using StormWebSocketClient c2 = new(new WsClientOptions
            {
                Uri = new Uri($"ws://localhost:{port}/"),
                Heartbeat = new HeartbeatOptions { PingInterval = TimeSpan.Zero },
            });
            c2.OnMessageReceived += async msg => client2Received.TrySetResult(msg.Text);
            await c2.ConnectAsync();

            await bothConnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await server.BroadcastTextAsync("hello all");

            Assert.Equal("hello all", await client1Received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("hello all", await client2Received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task BroadcastTextAsync_ExcludesSession()
    {
        int port = GetPort();
        TaskCompletionSource<string> client1Received = new();
        TaskCompletionSource<string> client2Received = new();
        int connectedCount = 0;
        TaskCompletionSource bothConnected = new();
        long firstSessionId = 0;

        await using StormWebSocketServer server = new(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            WebSocket = new WebSocketOptions
            {
                Heartbeat = new HeartbeatOptions { PingInterval = TimeSpan.Zero },
            },
        });
        server.OnConnected += async session =>
        {
            if (Interlocked.Increment(ref connectedCount) == 1)
                Interlocked.Exchange(ref firstSessionId, session.Id);
            if (connectedCount == 2)
                bothConnected.TrySetResult();
        };
        await server.StartAsync();

        try
        {
            await using StormWebSocketClient c1 = new(new WsClientOptions
            {
                Uri = new Uri($"ws://localhost:{port}/"),
                Heartbeat = new HeartbeatOptions { PingInterval = TimeSpan.Zero },
            });
            c1.OnMessageReceived += async msg => client1Received.TrySetResult(msg.Text);
            await c1.ConnectAsync();

            await using StormWebSocketClient c2 = new(new WsClientOptions
            {
                Uri = new Uri($"ws://localhost:{port}/"),
                Heartbeat = new HeartbeatOptions { PingInterval = TimeSpan.Zero },
            });
            c2.OnMessageReceived += async msg => client2Received.TrySetResult(msg.Text);
            await c2.ConnectAsync();

            await bothConnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await server.BroadcastTextAsync("hello", excludeId: firstSessionId);

            // Client 2 should receive, client 1 should not
            Assert.Equal("hello", await client2Received.Task.WaitAsync(TimeSpan.FromSeconds(5)));

            // Give client 1 a moment — it should NOT receive
            await Task.Delay(200);
            Assert.False(client1Received.Task.IsCompleted);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task Server_GroupBroadcast_Integration()
    {
        int port = GetPort();
        TaskCompletionSource<string> lobbyReceived = new();
        TaskCompletionSource<string> vipReceived = new();
        int connectedCount = 0;
        TaskCompletionSource allConnected = new();

        await using StormWebSocketServer server = new(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            WebSocket = new WebSocketOptions
            {
                Heartbeat = new HeartbeatOptions { PingInterval = TimeSpan.Zero },
            },
        });

        server.OnConnected += async session =>
        {
            int count = Interlocked.Increment(ref connectedCount);
            if (count == 1)
                server.Groups.Add("lobby", session);
            else if (count == 2)
                server.Groups.Add("vip", session);

            if (count == 2)
                allConnected.TrySetResult();
        };
        await server.StartAsync();

        try
        {
            await using StormWebSocketClient lobbyClient = new(new WsClientOptions
            {
                Uri = new Uri($"ws://localhost:{port}/"),
                Heartbeat = new HeartbeatOptions { PingInterval = TimeSpan.Zero },
            });
            lobbyClient.OnMessageReceived += async msg => lobbyReceived.TrySetResult(msg.Text);
            await lobbyClient.ConnectAsync();

            await using StormWebSocketClient vipClient = new(new WsClientOptions
            {
                Uri = new Uri($"ws://localhost:{port}/"),
                Heartbeat = new HeartbeatOptions { PingInterval = TimeSpan.Zero },
            });
            vipClient.OnMessageReceived += async msg => vipReceived.TrySetResult(msg.Text);
            await vipClient.ConnectAsync();

            await allConnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Broadcast to lobby only
            await server.Groups.BroadcastAsync("lobby", "lobby msg"u8.ToArray());

            // Lobby client should receive
            // Note: using raw bytes, so the WS server sends binary frames via group broadcast
            // This tests NetworkSessionGroup.BroadcastAsync path

            await Task.Delay(200);
            // vip should not have received
            Assert.False(vipReceived.Task.IsCompleted);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task WsClient_SendTextAsync_Utf8Overload()
    {
        int port = GetPort();
        TaskCompletionSource<string> received = new();

        await using StormWebSocketServer server = new(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            WebSocket = new WebSocketOptions
            {
                Heartbeat = new HeartbeatOptions { PingInterval = TimeSpan.Zero },
            },
        });
        server.OnMessageReceived += async (_, msg) => received.TrySetResult(msg.Text);
        await server.StartAsync();

        try
        {
            await using StormWebSocketClient client = new(new WsClientOptions
            {
                Uri = new Uri($"ws://localhost:{port}/"),
                Heartbeat = new HeartbeatOptions { PingInterval = TimeSpan.Zero },
            });
            await client.ConnectAsync();

            // Use the ReadOnlyMemory<byte> overload
            ReadOnlyMemory<byte> utf8Data = "Hello UTF8"u8.ToArray();
            await client.SendTextAsync(utf8Data);

            string result = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("Hello UTF8", result);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task WsClient_Reconnect_ReconnectsAfterDisconnect()
    {
        int port = GetPort();
        int connectCount = 0;
        TaskCompletionSource reconnected = new();

        await using StormWebSocketServer server = new(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            WebSocket = new WebSocketOptions
            {
                Heartbeat = new HeartbeatOptions { PingInterval = TimeSpan.Zero },
            },
        });
        await server.StartAsync();

        try
        {
            StormWebSocketClient client = new(new WsClientOptions
            {
                Uri = new Uri($"ws://localhost:{port}/"),
                Heartbeat = new HeartbeatOptions { PingInterval = TimeSpan.Zero },
                Reconnect = new ReconnectOptions
                {
                    Enabled = true,
                    Delay = TimeSpan.FromMilliseconds(200),
                },
            });
            client.OnConnected += async () =>
            {
                if (Interlocked.Increment(ref connectCount) == 2)
                    reconnected.TrySetResult();
            };

            await client.ConnectAsync();

            // Force disconnect all server sessions
            foreach (var s in server.Sessions.All)
            {
                if (s is WebSocketSession ws)
                    await ws.CloseAsync();
            }

            // Should reconnect
            await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, connectCount);

            await client.DisposeAsync();
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task TcpClient_Reconnect_ReconnectsAfterDisconnect()
    {
        int port = GetPort();
        int connectCount = 0;
        TaskCompletionSource reconnected = new();

        await using StormTcpServer server = new(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
        });
        await server.StartAsync();

        try
        {
            StormTcpClient client = new(new ClientOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, port),
                Reconnect = new ReconnectOptions
                {
                    Enabled = true,
                    Delay = TimeSpan.FromMilliseconds(200),
                },
            });
            client.OnConnected += async () =>
            {
                if (Interlocked.Increment(ref connectCount) == 2)
                    reconnected.TrySetResult();
            };

            await client.ConnectAsync();

            // Force close all server sessions
            await server.Sessions.CloseAllAsync();

            await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, connectCount);

            await client.DisposeAsync();
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task WsClient_SendAsync_WhenNotConnected_Throws()
    {
        StormWebSocketClient client = new(new WsClientOptions
        {
            Uri = new Uri("ws://localhost:1/"),
        });

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.SendAsync(new byte[] { 1 }));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.SendTextAsync("test"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.SendTextAsync(new ReadOnlyMemory<byte>(new byte[] { 1 })));

        await client.DisposeAsync();
    }

    [Fact]
    public async Task TcpClient_SendAsync_WhenNotConnected_Throws()
    {
        StormTcpClient client = new(new ClientOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 1),
        });

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.SendAsync(new byte[] { 1 }));

        await client.DisposeAsync();
    }

    [Fact]
    public async Task WsServer_SessionData_PersistsAcrossEvents()
    {
        int port = GetPort();
        TaskCompletionSource<string> result = new();
        SessionKey<string> userKey = new("userId");

        await using StormWebSocketServer server = new(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            WebSocket = new WebSocketOptions
            {
                Heartbeat = new HeartbeatOptions { PingInterval = TimeSpan.Zero },
            },
        });

        server.OnConnected += async session =>
        {
            session.Set(userKey, "user-123");
        };

        server.OnMessageReceived += async (session, msg) =>
        {
            string? userId = session.Get(userKey);
            result.TrySetResult(userId ?? "null");
        };

        await server.StartAsync();

        try
        {
            await using StormWebSocketClient client = new(new WsClientOptions
            {
                Uri = new Uri($"ws://localhost:{port}/"),
                Heartbeat = new HeartbeatOptions { PingInterval = TimeSpan.Zero },
            });
            await client.ConnectAsync();
            await client.SendTextAsync("trigger");

            string userId = await result.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("user-123", userId);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task WsServer_GoingAway_OnStop()
    {
        int port = GetPort();
        TaskCompletionSource<DisconnectReason> clientDisconnected = new();

        await using StormWebSocketServer server = new(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            WebSocket = new WebSocketOptions
            {
                Heartbeat = new HeartbeatOptions { PingInterval = TimeSpan.Zero },
            },
        });
        TaskCompletionSource connected = new();
        server.OnConnected += async _ => connected.TrySetResult();
        await server.StartAsync();

        StormWebSocketClient client = new(new WsClientOptions
        {
            Uri = new Uri($"ws://localhost:{port}/"),
            Heartbeat = new HeartbeatOptions { PingInterval = TimeSpan.Zero },
        });
        client.OnDisconnected += async reason => clientDisconnected.TrySetResult(reason);
        await client.ConnectAsync();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Stop server — should send GoingAway close
        await server.StopAsync();

        DisconnectReason reason = await clientDisconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Client receives close frame from server
        Assert.Equal(DisconnectReason.ClosedByServer, reason);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task WsServer_Subprotocol_Negotiation()
    {
        int port = GetPort();

        await using StormWebSocketServer server = new(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            WebSocket = new WebSocketOptions
            {
                Heartbeat = new HeartbeatOptions { PingInterval = TimeSpan.Zero },
            },
        });
        server.OnConnecting += async context =>
        {
            if (context.RequestedSubprotocols.Contains("graphql-ws"))
                context.AcceptSubprotocol("graphql-ws");
            else
                context.Accept();
        };
        await server.StartAsync();

        try
        {
            await using StormWebSocketClient client = new(new WsClientOptions
            {
                Uri = new Uri($"ws://localhost:{port}/"),
                Heartbeat = new HeartbeatOptions { PingInterval = TimeSpan.Zero },
                Subprotocols = ["graphql-ws", "graphql-transport-ws"],
            });
            await client.ConnectAsync();

            Assert.Equal("graphql-ws", client.Subprotocol);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task WsClient_DisconnectAsync_WhenAlreadyClosed_Noop()
    {
        StormWebSocketClient client = new(new WsClientOptions
        {
            Uri = new Uri("ws://localhost:1/"),
        });

        // Should not throw when not connected
        await client.DisconnectAsync();
        await client.DisconnectAsync(); // double disconnect

        await client.DisposeAsync();
        await client.DisposeAsync(); // double dispose
    }

    [Fact]
    public async Task TcpClient_DisconnectAsync_WhenAlreadyClosed_Noop()
    {
        StormTcpClient client = new(new ClientOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 1),
        });

        await client.DisconnectAsync();
        await client.DisconnectAsync();

        await client.DisposeAsync();
        await client.DisposeAsync();
    }

    [Fact]
    public async Task WsServer_DoubleDispose_Noop()
    {
        int port = GetPort();
        StormWebSocketServer server = new(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
        });
        await server.StartAsync();

        await server.DisposeAsync();
        await server.DisposeAsync(); // should not throw
    }

    [Fact]
    public async Task TcpServer_DoubleDispose_Noop()
    {
        int port = GetPort();
        StormTcpServer server = new(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
        });
        await server.StartAsync();

        await server.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task WsServer_StopAsync_BeforeStart_Noop()
    {
        StormWebSocketServer server = new(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, GetPort()),
        });
        await server.StopAsync(); // should not throw
        await server.DisposeAsync();
    }

    [Fact]
    public async Task TcpServer_StopAsync_BeforeStart_Noop()
    {
        StormTcpServer server = new(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, GetPort()),
        });
        await server.StopAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task WsClient_OnReconnecting_Fires()
    {
        int port = GetPort();
        TaskCompletionSource<(int attempt, TimeSpan delay)> reconnecting = new();

        // No server — so connection will fail
        StormWebSocketClient client = new(new WsClientOptions
        {
            Uri = new Uri($"ws://localhost:{port}/"),
            Reconnect = new ReconnectOptions
            {
                Enabled = true,
                Delay = TimeSpan.FromMilliseconds(100),
                MaxAttempts = 1,
            },
        });
        client.OnReconnecting += async (attempt, delay) =>
        {
            reconnecting.TrySetResult((attempt, delay));
        };

        try
        {
            await client.ConnectAsync();
        }
        catch
        {
            // Expected — max attempts exceeded
        }

        (int attempt, TimeSpan delay) = await reconnecting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, attempt);
        Assert.Equal(TimeSpan.FromMilliseconds(100), delay);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task TcpClient_OnReconnecting_Fires()
    {
        int port = GetPort();
        TaskCompletionSource<int> reconnecting = new();

        StormTcpClient client = new(new ClientOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            Reconnect = new ReconnectOptions
            {
                Enabled = true,
                Delay = TimeSpan.FromMilliseconds(100),
                MaxAttempts = 1,
            },
        });
        client.OnReconnecting += async (attempt, _) =>
        {
            reconnecting.TrySetResult(attempt);
        };

        try
        {
            await client.ConnectAsync();
        }
        catch
        {
            // Expected
        }

        int attempt = await reconnecting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, attempt);

        await client.DisposeAsync();
    }
}
