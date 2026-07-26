using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using StormSocket.Client;
using StormSocket.Server;
using Xunit;

namespace StormSocket.Tests;

/// <summary>
/// Covers <c>StartAsync</c>/<c>StopAsync</c> lifecycle: the graceful drain, its two bounds, the
/// double-start guard and the state a failed bind leaves behind.
/// </summary>
public class ServerLifecycleTests
{
    [Fact]
    public async Task StopAsync_WaitsForHandlerThatIsMidFlight()
    {
        TaskCompletionSource handlerEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource handlerFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using StormTcpServer server = new(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
        });

        server.OnDataReceived += async (_, _) =>
        {
            handlerEntered.TrySetResult();
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            handlerFinished.TrySetResult();
        };

        await server.StartAsync();
        Assert.True(server.IsRunning);

        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)server.LocalEndPoint!).Port);
        await client.GetStream().WriteAsync("drain"u8.ToArray());

        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await server.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(handlerFinished.Task.IsCompletedSuccessfully, "StopAsync returned while the handler was still running");
        Assert.False(server.IsRunning);
    }

    [Fact]
    public async Task StopAsync_WaitsForWebSocketHandlerThatIsMidFlight()
    {
        TaskCompletionSource handlerEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource handlerFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using StormWebSocketServer server = new(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            WebSocket = new WebSocketOptions { Heartbeat = new() { PingInterval = TimeSpan.Zero } },
        });

        server.OnMessageReceived += async (_, _) =>
        {
            handlerEntered.TrySetResult();
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            handlerFinished.TrySetResult();
        };

        await server.StartAsync();

        await using StormWebSocketClient client = new(new WsClientOptions
        {
            Uri = new Uri($"ws://127.0.0.1:{((IPEndPoint)server.LocalEndPoint!).Port}"),
            Heartbeat = new() { PingInterval = TimeSpan.Zero },
        });
        await client.ConnectAsync();
        await client.SendTextAsync("drain");

        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await server.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(handlerFinished.Task.IsCompletedSuccessfully, "StopAsync returned while the handler was still running");
        Assert.False(server.IsRunning);
    }

    [Fact]
    public async Task StopAsync_GivesUpOnTheDrainTimeout_WithoutThrowing()
    {
        TaskCompletionSource handlerEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseHandler = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using StormTcpServer server = new(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ShutdownDrainTimeout = TimeSpan.FromMilliseconds(200),
        });

        server.OnDataReceived += async (_, _) =>
        {
            handlerEntered.TrySetResult();
            await releaseHandler.Task;
        };

        await server.StartAsync();

        try
        {
            using TcpClient client = new();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)server.LocalEndPoint!).Port);
            await client.GetStream().WriteAsync("stuck"u8.ToArray());

            await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            long start = Stopwatch.GetTimestamp();
            await server.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
            TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

            Assert.True(elapsed < TimeSpan.FromSeconds(10), $"StopAsync ignored the drain timeout and took {elapsed}");
            Assert.False(server.IsRunning);
        }
        finally
        {
            releaseHandler.TrySetResult();
        }
    }

    [Fact]
    public async Task StopAsync_StopsWaitingWhenTheCallersTokenIsCancelled()
    {
        TaskCompletionSource handlerEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseHandler = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using StormTcpServer server = new(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0),

            // The token, not this, is what has to end the wait.
            ShutdownDrainTimeout = Timeout.InfiniteTimeSpan,
        });

        server.OnDataReceived += async (_, _) =>
        {
            handlerEntered.TrySetResult();
            await releaseHandler.Task;
        };

        await server.StartAsync();

        try
        {
            using TcpClient client = new();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)server.LocalEndPoint!).Port);
            await client.GetStream().WriteAsync("stuck"u8.ToArray());

            await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            using CancellationTokenSource shutdown = new(TimeSpan.FromMilliseconds(200));

            // A host's shutdown deadline firing is a normal "force it" signal, never an exception.
            await server.StopAsync(shutdown.Token).WaitAsync(TimeSpan.FromSeconds(30));

            Assert.False(server.IsRunning);
        }
        finally
        {
            releaseHandler.TrySetResult();
        }
    }

    [Fact]
    public async Task StopAsync_IsANoOp_WhenTheServerWasNeverStarted()
    {
        await using StormTcpServer tcp = new(new ServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) });
        await using StormWebSocketServer ws = new(new ServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) });

        Assert.False(tcp.IsRunning);
        Assert.False(ws.IsRunning);

        await tcp.StopAsync();
        await ws.StopAsync();
    }

    [Fact]
    public async Task StartAsync_Twice_Throws()
    {
        await using StormTcpServer server = new(new ServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) });

        await server.StartAsync();
        EndPoint? boundTo = server.LocalEndPoint;

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => server.StartAsync());
        Assert.Contains("already running", ex.Message, StringComparison.OrdinalIgnoreCase);

        // The rejected start must not have replaced the listener that is already serving.
        Assert.Equal(boundTo, server.LocalEndPoint);
        Assert.True(server.IsRunning);

        await server.StopAsync();
    }

    [Fact]
    public async Task StartAsync_Twice_Throws_OnWebSocketServer()
    {
        await using StormWebSocketServer server = new(new ServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) });

        await server.StartAsync();

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => server.StartAsync());
        Assert.Contains("already running", ex.Message, StringComparison.OrdinalIgnoreCase);

        await server.StopAsync();
    }

    [Fact]
    public async Task StartAsync_AfterStopAsync_StartsAgain_AndStillDrains()
    {
        TaskCompletionSource handlerEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource handlerFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using StormTcpServer server = new(new ServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) });

        server.OnDataReceived += async (_, _) =>
        {
            handlerEntered.TrySetResult();
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            handlerFinished.TrySetResult();
        };

        await server.StartAsync();
        await server.StopAsync();
        Assert.False(server.IsRunning);

        await server.StartAsync();
        Assert.True(server.IsRunning);

        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)server.LocalEndPoint!).Port);
        await client.GetStream().WriteAsync("drain"u8.ToArray());

        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await server.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));

        // The restart has to re-arm the drain, not inherit the sealed state of the first shutdown.
        Assert.True(handlerFinished.Task.IsCompletedSuccessfully, "StopAsync returned while the handler was still running");
    }

    [Fact]
    public async Task StartAsync_FailedBind_LeavesTheServerStopped()
    {
        await using StormTcpServer server = new(new ServerOptions
        {
            // RFC 5737 documentation address: never assigned to a local interface, so the bind fails.
            EndPoint = new IPEndPoint(IPAddress.Parse("192.0.2.1"), 5000),
        });

        await Assert.ThrowsAsync<SocketException>(() => server.StartAsync());

        Assert.False(server.IsRunning);
        Assert.Null(server.LocalEndPoint);

        // Nothing was left half-started, so the same instance can still be started properly.
        await server.StopAsync();
    }

    [Fact]
    public async Task StartAsync_FailedBind_LeavesTheWebSocketServerStopped()
    {
        await using StormWebSocketServer server = new(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Parse("192.0.2.1"), 5000),
        });

        await Assert.ThrowsAsync<SocketException>(() => server.StartAsync());

        Assert.False(server.IsRunning);
        Assert.Null(server.LocalEndPoint);
    }

    [Fact]
    public async Task StartAsync_InvalidOptions_LeavesTheServerStopped()
    {
        await using StormTcpServer server = new(new ServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            Ssl = new SslOptions(),
        });

        ArgumentException ex = await Assert.ThrowsAnyAsync<ArgumentException>(() => server.StartAsync());
        Assert.Contains("SslOptions.Certificate", ex.Message, StringComparison.Ordinal);

        Assert.False(server.IsRunning);
        Assert.Null(server.LocalEndPoint);
    }
}
