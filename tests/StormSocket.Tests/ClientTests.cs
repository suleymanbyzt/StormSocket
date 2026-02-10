using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using StormSocket.Client;
using StormSocket.Framing;
using StormSocket.Server;
using StormSocket.Session;
using StormSocket.WebSocket;
using Xunit;

namespace StormSocket.Tests;

public class ClientTests
{
    private static int _nextPort = 17000;
    private static int GetPort() => Interlocked.Increment(ref _nextPort);

    [Fact]
    public async Task TcpClient_ConnectSendReceive()
    {
        int port = GetPort();
        TaskCompletionSource<byte[]> received = new();

        await using StormTcpServer server = new StormTcpServer(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
        });
        server.OnDataReceived += async (session, data) =>
        {
            // Echo back
            await session.SendAsync(data);
        };
        await server.StartAsync();

        await using StormTcpClient client = new StormTcpClient(new ClientOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
        });
        client.OnDataReceived += async data =>
        {
            received.TrySetResult(data.ToArray());
        };

        await client.ConnectAsync();

        byte[] sent = Encoding.UTF8.GetBytes("Hello StormSocket");
        await client.SendAsync(sent);

        byte[] result = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(sent, result);
    }

    [Fact]
    public async Task TcpClient_WithLengthPrefixFramer()
    {
        int port = GetPort();
        TaskCompletionSource<byte[]> received = new();
        LengthPrefixFramer framer = new LengthPrefixFramer();

        await using StormTcpServer server = new StormTcpServer(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            Framer = framer,
        });
        server.OnDataReceived += async (session, data) =>
        {
            await session.SendAsync(data);
        };
        await server.StartAsync();

        await using StormTcpClient client = new StormTcpClient(new ClientOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            Framer = framer,
        });
        client.OnDataReceived += async data =>
        {
            received.TrySetResult(data.ToArray());
        };

        await client.ConnectAsync();

        byte[] sent = Encoding.UTF8.GetBytes("Framed message!");
        await client.SendAsync(sent);

        byte[] result = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(sent, result);
    }

    [Fact]
    public async Task TcpClient_ConnectDisconnect_FiresEvents()
    {
        int port = GetPort();
        TaskCompletionSource connectedTcs = new();
        TaskCompletionSource disconnectedTcs = new();

        await using StormTcpServer server = new StormTcpServer(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
        });
        await server.StartAsync();

        StormTcpClient client = new StormTcpClient(new ClientOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
        });
        client.OnConnected += async () => connectedTcs.TrySetResult();
        client.OnDisconnected += async () => disconnectedTcs.TrySetResult();

        await client.ConnectAsync();
        await connectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await client.DisconnectAsync();
        await disconnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await client.DisposeAsync();
    }

    [Fact]
    public async Task WsClient_ConnectSendReceive()
    {
        int port = GetPort();
        TaskCompletionSource<string> received = new();

        await using StormWebSocketServer server = new StormWebSocketServer(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
        });
        server.OnMessageReceived += async (session, msg) =>
        {
            // Echo back as text via WebSocketSession.SendTextAsync
            if (session is Session.WebSocketSession wss)
            {
                await wss.SendTextAsync(msg.Data);
            }
        };
        await server.StartAsync();

        await using StormWebSocketClient client = new StormWebSocketClient(new WsClientOptions
        {
            Uri = new Uri($"ws://localhost:{port}/"),
            Heartbeat = new StormSocket.Core.HeartbeatOptions { PingInterval = TimeSpan.Zero }, // disable for test
        });
        client.OnMessageReceived += async msg =>
        {
            received.TrySetResult(msg.Text);
        };

        await client.ConnectAsync();
        await client.SendTextAsync("Hello WebSocket!");

        string result = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Hello WebSocket!", result);
    }

    [Fact]
    public async Task WsClient_BinarySendReceive()
    {
        int port = GetPort();
        TaskCompletionSource<byte[]> received = new();

        await using StormWebSocketServer server = new StormWebSocketServer(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
        });
        server.OnMessageReceived += async (session, msg) =>
        {
            await session.SendAsync(msg.Data);
        };
        await server.StartAsync();

        await using StormWebSocketClient client = new StormWebSocketClient(new WsClientOptions
        {
            Uri = new Uri($"ws://localhost:{port}/"),
            Heartbeat = new StormSocket.Core.HeartbeatOptions { PingInterval = TimeSpan.Zero },
        });
        client.OnMessageReceived += async msg =>
        {
            received.TrySetResult(msg.Data.ToArray());
        };

        await client.ConnectAsync();

        byte[] sent = [1, 2, 3, 4, 5];
        await client.SendAsync(sent);

        byte[] result = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(sent, result);
    }

    [Fact]
    public async Task WsClient_ConnectDisconnect_FiresEvents()
    {
        int port = GetPort();
        TaskCompletionSource connectedTcs = new();
        TaskCompletionSource disconnectedTcs = new();

        await using StormWebSocketServer server = new StormWebSocketServer(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
        });
        await server.StartAsync();

        StormWebSocketClient client = new StormWebSocketClient(new WsClientOptions
        {
            Uri = new Uri($"ws://localhost:{port}/"),
            Heartbeat = new StormSocket.Core.HeartbeatOptions { PingInterval = TimeSpan.Zero },
        });
        client.OnConnected += async () => connectedTcs.TrySetResult();
        client.OnDisconnected += async () => disconnectedTcs.TrySetResult();

        await client.ConnectAsync();
        await connectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await client.DisconnectAsync();
        await disconnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await client.DisposeAsync();
    }

    [Fact]
    public async Task MaskedFrame_RoundTrip()
    {
        // Write a masked frame and decode it, verify payload matches
        Pipe pipe = new Pipe();

        byte[] payload = Encoding.UTF8.GetBytes("masked test payload");
        WsFrameEncoder.WriteMaskedText(pipe.Writer, payload);
        await pipe.Writer.FlushAsync();
        await pipe.Writer.CompleteAsync();

        ReadResult readResult = await pipe.Reader.ReadAsync();
        ReadOnlySequence<byte> buffer = readResult.Buffer;

        Assert.True(WsFrameDecoder.TryDecodeFrame(ref buffer, out WsFrame frame));
        Assert.True(frame.Masked);
        Assert.Equal(WsOpCode.Text, frame.OpCode);
        Assert.Equal(payload, frame.Payload.ToArray());

        pipe.Reader.AdvanceTo(buffer.Start, buffer.End);
        await pipe.Reader.CompleteAsync();
    }

    [Fact]
    public async Task MaskedClose_RoundTrip()
    {
        Pipe pipe = new Pipe();

        WsFrameEncoder.WriteMaskedClose(pipe.Writer, WsCloseStatus.NormalClosure);
        await pipe.Writer.FlushAsync();
        await pipe.Writer.CompleteAsync();

        ReadResult readResult = await pipe.Reader.ReadAsync();
        ReadOnlySequence<byte> buffer = readResult.Buffer;

        Assert.True(WsFrameDecoder.TryDecodeFrame(ref buffer, out WsFrame frame));
        Assert.True(frame.Masked);
        Assert.Equal(WsOpCode.Close, frame.OpCode);
        Assert.Equal(2, frame.Payload.Length);

        ushort code = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(frame.Payload.Span);
        Assert.Equal((ushort)WsCloseStatus.NormalClosure, code);

        pipe.Reader.AdvanceTo(buffer.Start, buffer.End);
        await pipe.Reader.CompleteAsync();
    }

    [Fact]
    public void ClientUpgradeRequest_BuildAndValidate()
    {
        Uri uri = new Uri("ws://example.com:8080/chat");
        (byte[] request, string wsKey) = WsUpgradeHandler.BuildUpgradeRequest(uri);

        string requestStr = Encoding.ASCII.GetString(request);

        Assert.Contains("GET /chat HTTP/1.1\r\n", requestStr);
        Assert.Contains("Host: example.com:8080\r\n", requestStr);
        Assert.Contains("Upgrade: websocket\r\n", requestStr);
        Assert.Contains("Connection: Upgrade\r\n", requestStr);
        Assert.Contains($"Sec-WebSocket-Key: {wsKey}\r\n", requestStr);
        Assert.Contains("Sec-WebSocket-Version: 13\r\n", requestStr);
        Assert.EndsWith("\r\n\r\n", requestStr);
    }

    [Fact]
    public async Task ClientUpgradeResponse_ParseValid()
    {
        string wsKey = "dGhlIHNhbXBsZSBub25jZQ==";
        byte[] responseBytes = WsUpgradeHandler.BuildUpgradeResponse(wsKey);

        Pipe pipe = new Pipe();
        Span<byte> span = pipe.Writer.GetSpan(responseBytes.Length);
        responseBytes.CopyTo(span);
        pipe.Writer.Advance(responseBytes.Length);
        await pipe.Writer.FlushAsync();
        await pipe.Writer.CompleteAsync();

        ReadResult readResult = await pipe.Reader.ReadAsync();
        ReadOnlySequence<byte> buffer = readResult.Buffer;

        Assert.True(WsUpgradeHandler.TryParseUpgradeResponse(ref buffer, wsKey));

        pipe.Reader.AdvanceTo(buffer.Start, buffer.End);
        await pipe.Reader.CompleteAsync();
    }

    [Fact]
    public async Task Heartbeat_KeepsConnectionAlive()
    {
        int port = GetPort();
        TaskCompletionSource serverConnected = new();
        TaskCompletionSource serverDisconnected = new();
        WebSocketSession? serverSession = null;

        await using StormWebSocketServer server = new StormWebSocketServer(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            WebSocket = new WebSocketOptions
            {
                Heartbeat = new StormSocket.Core.HeartbeatOptions
                {
                    PingInterval = TimeSpan.FromMilliseconds(200),
                    MaxMissedPongs = 3,
                },
            },
        });
        server.OnConnected += async session =>
        {
            serverSession = (WebSocketSession)session;
            serverConnected.TrySetResult();
        };
        server.OnDisconnected += async _ => serverDisconnected.TrySetResult();
        await server.StartAsync();

        // Client with AutoPong=true (default) will respond to pings
        await using StormWebSocketClient client = new StormWebSocketClient(new WsClientOptions
        {
            Uri = new Uri($"ws://localhost:{port}/"),
            Heartbeat = new StormSocket.Core.HeartbeatOptions { PingInterval = TimeSpan.Zero }, // client doesn't send its own pings
        });
        await client.ConnectAsync();
        await serverConnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Wait for several ping/pong cycles (200ms * 4 = 800ms)
        await Task.Delay(800);

        // Connection should still be alive pongs are resetting the counter
        Assert.Equal(StormSocket.Core.ConnectionState.Connected, client.State);
        Assert.Equal(1, server.Sessions.Count);

        // Verify server didn't disconnect us
        Assert.False(serverDisconnected.Task.IsCompleted);
    }

    [Fact]
    public async Task Heartbeat_DeadConnection_ClosesAfterMissedPongs()
    {
        int port = GetPort();
        TaskCompletionSource serverDisconnected = new();

        await using StormWebSocketServer server = new StormWebSocketServer(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            WebSocket = new WebSocketOptions
            {
                Heartbeat = new StormSocket.Core.HeartbeatOptions
                {
                    PingInterval = TimeSpan.FromMilliseconds(100),
                    MaxMissedPongs = 2,
                    AutoPong = true,
                },
            },
        });
        server.OnDisconnected += async _ => serverDisconnected.TrySetResult();
        await server.StartAsync();

        // Connect a raw TCP socket does WebSocket upgrade but NEVER sends pong
        using System.Net.Sockets.TcpClient raw = new();
        await raw.ConnectAsync(IPAddress.Loopback, port);
        System.Net.Sockets.NetworkStream stream = raw.GetStream();

        // Send WebSocket upgrade request manually
        Uri uri = new Uri($"ws://localhost:{port}/");
        (byte[] request, string wsKey) = WsUpgradeHandler.BuildUpgradeRequest(uri);
        await stream.WriteAsync(request);
        await stream.FlushAsync();

        // Read upgrade response
        byte[] responseBuf = new byte[1024];
        int read = await stream.ReadAsync(responseBuf);
        string response = Encoding.ASCII.GetString(responseBuf, 0, read);
        Assert.Contains("101", response);

        // Now the server thinks we're a WebSocket client
        // Server will send Ping every 100ms, we never send Pong
        // After MaxMissedPongs(2) + 1 tick = ~300ms, server should close us

        // Wait for server to detect dead connection and disconnect
        await serverDisconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Server should have removed the session
        Assert.Equal(0, server.Sessions.Count);
    }

    [Fact]
    public void ClientUpgradeRequest_WithAdditionalHeaders()
    {
        Uri uri = new Uri("ws://localhost/");
        Dictionary<string, string> headers = new()
        {
            { "Authorization", "Bearer token123" },
            { "X-Custom", "value" },
        };

        (byte[] request, string _) = WsUpgradeHandler.BuildUpgradeRequest(uri, headers);
        string requestStr = Encoding.ASCII.GetString(request);

        Assert.Contains("Authorization: Bearer token123\r\n", requestStr);
        Assert.Contains("X-Custom: value\r\n", requestStr);
    }

    /// <summary>
    /// Helper: connects a raw TCP socket, performs WS upgrade, returns session + stream.
    /// The raw client never reads after handshake, simulating a slow consumer.
    /// </summary>
    private static async Task<(System.Net.Sockets.TcpClient raw, System.Net.Sockets.NetworkStream stream)>
        ConnectRawWebSocket(int port)
    {
        System.Net.Sockets.TcpClient raw = new() { ReceiveBufferSize = 1024 };
        await raw.ConnectAsync(IPAddress.Loopback, port);
        System.Net.Sockets.NetworkStream stream = raw.GetStream();

        Uri uri = new Uri($"ws://localhost:{port}/");
        (byte[] request, string wsKey) = WsUpgradeHandler.BuildUpgradeRequest(uri);
        await stream.WriteAsync(request);
        await stream.FlushAsync();

        byte[] responseBuf = new byte[1024];
        int read = await stream.ReadAsync(responseBuf);
        string response = Encoding.ASCII.GetString(responseBuf, 0, read);
        if (!response.Contains("101"))
        {
            throw new Exception("WebSocket upgrade failed");
        }

        return (raw, stream);
    }

    [Fact]
    public async Task WsServer_DropPolicy_SkipsSlowClient()
    {
        int port = GetPort();

        await using StormWebSocketServer server = new StormWebSocketServer(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            Socket = new StormSocket.Core.SocketTuningOptions { MaxPendingSendBytes = 1024 },
            SlowConsumerPolicy = StormSocket.Core.SlowConsumerPolicy.Drop,
            WebSocket = new WebSocketOptions { Heartbeat = new StormSocket.Core.HeartbeatOptions { PingInterval = TimeSpan.Zero } },
        });

        TaskCompletionSource<WebSocketSession> connected = new();
        server.OnConnected += async session => connected.TrySetResult((WebSocketSession)session);
        await server.StartAsync();

        try
        {
            // Slow client connects but NEVER reads after handshake
            (System.Net.Sockets.TcpClient raw, System.Net.Sockets.NetworkStream _) = await ConnectRawWebSocket(port);
            using System.Net.Sockets.TcpClient slowClient = raw;
            WebSocketSession slowSession = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Flood data until backpressure kicks in
            byte[] chunk = new byte[4096];
            using CancellationTokenSource floodCts = new();
            Task floodTask = Task.Run(async () =>
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

            // Wait for backpressure
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

            Assert.True(backpressured, "Slow WS client should be backpressured");

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
    public async Task WsServer_DisconnectPolicy_ClosesSlowClient()
    {
        int port = GetPort();

        await using StormWebSocketServer server = new StormWebSocketServer(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            Socket = new StormSocket.Core.SocketTuningOptions { MaxPendingSendBytes = 1024 },
            SlowConsumerPolicy = StormSocket.Core.SlowConsumerPolicy.Disconnect,
            WebSocket = new WebSocketOptions { Heartbeat = new StormSocket.Core.HeartbeatOptions { PingInterval = TimeSpan.Zero } },
        });

        TaskCompletionSource<WebSocketSession> connected = new();
        TaskCompletionSource disconnected = new();
        server.OnConnected += async session => connected.TrySetResult((WebSocketSession)session);
        server.OnDisconnected += async _ => disconnected.TrySetResult();
        await server.StartAsync();

        try
        {
            // Slow client connects but NEVER reads after handshake
            (System.Net.Sockets.TcpClient raw, System.Net.Sockets.NetworkStream _) = await ConnectRawWebSocket(port);
            using System.Net.Sockets.TcpClient slowClient = raw;
            WebSocketSession slowSession = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Flood data until backpressure triggers Disconnect
            byte[] chunk = new byte[1024 * 64];
            using CancellationTokenSource floodCts = new();
            Task floodTask = Task.Run(async () =>
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

    [Fact]
    public async Task WsServer_HandshakeTimeout_ClosesIdleConnection()
    {
        int port = GetPort();

        await using StormWebSocketServer server = new StormWebSocketServer(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            WebSocket = new WebSocketOptions
            {
                Heartbeat = new StormSocket.Core.HeartbeatOptions { PingInterval = TimeSpan.Zero },
                HandshakeTimeout = TimeSpan.FromMilliseconds(500),
            },
        });

        TaskCompletionSource connected = new();
        server.OnConnected += async _ => connected.TrySetResult();
        await server.StartAsync();

        try
        {
            // Open raw TCP connection but never send the upgrade request
            using System.Net.Sockets.TcpClient raw = new();
            await raw.ConnectAsync(IPAddress.Loopback, port);
            System.Net.Sockets.NetworkStream stream = raw.GetStream();

            // The server should close the connection after HandshakeTimeout
            byte[] buf = new byte[1];
            int read = await stream.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            // Stream closed = 0 bytes read
            Assert.Equal(0, read);

            // OnConnected should never have fired
            Assert.False(connected.Task.IsCompleted);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task WsServer_HandshakeTimeout_AllowsTimelyUpgrade()
    {
        int port = GetPort();

        await using StormWebSocketServer server = new StormWebSocketServer(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            WebSocket = new WebSocketOptions
            {
                Heartbeat = new StormSocket.Core.HeartbeatOptions { PingInterval = TimeSpan.Zero },
                HandshakeTimeout = TimeSpan.FromSeconds(5),
            },
        });

        TaskCompletionSource connected = new();
        server.OnConnected += async _ => connected.TrySetResult();
        await server.StartAsync();

        try
        {
            // Connect and immediately send upgrade - should succeed
            (System.Net.Sockets.TcpClient raw, System.Net.Sockets.NetworkStream _) = await ConnectRawWebSocket(port);
            using System.Net.Sockets.TcpClient client = raw;

            await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(connected.Task.IsCompleted);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task WsServer_HandshakeTimeout_PartialRequestTimesOut()
    {
        int port = GetPort();

        await using StormWebSocketServer server = new StormWebSocketServer(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            WebSocket = new WebSocketOptions
            {
                Heartbeat = new StormSocket.Core.HeartbeatOptions { PingInterval = TimeSpan.Zero },
                HandshakeTimeout = TimeSpan.FromMilliseconds(500),
            },
        });

        TaskCompletionSource connected = new();
        server.OnConnected += async _ => connected.TrySetResult();
        await server.StartAsync();

        try
        {
            // Send partial upgrade request (no terminating \r\n\r\n)
            using System.Net.Sockets.TcpClient raw = new();
            await raw.ConnectAsync(IPAddress.Loopback, port);
            System.Net.Sockets.NetworkStream stream = raw.GetStream();

            byte[] partial = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\n");
            await stream.WriteAsync(partial);
            await stream.FlushAsync();

            // Server should close after timeout
            byte[] buf = new byte[1];
            int read = await stream.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, read);
            Assert.False(connected.Task.IsCompleted);
        }
        finally
        {
            await server.StopAsync();
        }
    }
}