using System.Net;
using StormSocket.Core;
using StormSocket.Middleware;
using StormSocket.Session;
using Xunit;

namespace StormSocket.Tests;

public class MiddlewarePipelineTests
{
    private sealed class FakeSession : ISession
    {
        public long Id => 1;
        public ConnectionState State => ConnectionState.Connected;
        public DisconnectReason DisconnectReason => DisconnectReason.None;
        public ConnectionMetrics Metrics { get; } = new();
        public EndPoint? RemoteEndPoint => null;
        public bool IsBackpressured => false;
        public IReadOnlySet<string> Groups => new HashSet<string>();
        public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();

        public T? Get<T>(SessionKey<T> key) => default;
        public void Set<T>(SessionKey<T> key, T value) { }
        public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask CloseAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public void Abort() { }
        public void JoinGroup(string group) { }
        public void LeaveGroup(string group) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TrackingMiddleware : IConnectionMiddleware
    {
        public bool ConnectedCalled { get; private set; }
        public bool DisconnectedCalled { get; private set; }
        public bool ErrorCalled { get; private set; }
        public bool DataReceivedCalled { get; private set; }
        public bool DataSendingCalled { get; private set; }
        public DisconnectReason? LastReason { get; private set; }
        public Exception? LastError { get; private set; }

        public ValueTask OnConnectedAsync(ISession session)
        {
            ConnectedCalled = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask<ReadOnlyMemory<byte>> OnDataReceivedAsync(ISession session, ReadOnlyMemory<byte> data)
        {
            DataReceivedCalled = true;
            return ValueTask.FromResult(data);
        }

        public ValueTask<ReadOnlyMemory<byte>> OnDataSendingAsync(ISession session, ReadOnlyMemory<byte> data)
        {
            DataSendingCalled = true;
            return ValueTask.FromResult(data);
        }

        public ValueTask OnDisconnectedAsync(ISession session, DisconnectReason reason)
        {
            DisconnectedCalled = true;
            LastReason = reason;
            return ValueTask.CompletedTask;
        }

        public ValueTask OnErrorAsync(ISession session, Exception exception)
        {
            ErrorCalled = true;
            LastError = exception;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SuppressMiddleware : IConnectionMiddleware
    {
        public ValueTask<ReadOnlyMemory<byte>> OnDataReceivedAsync(ISession session, ReadOnlyMemory<byte> data)
            => ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);

        public ValueTask<ReadOnlyMemory<byte>> OnDataSendingAsync(ISession session, ReadOnlyMemory<byte> data)
            => ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
    }

    [Fact]
    public async Task OnConnected_CallsAllMiddlewares()
    {
        MiddlewarePipeline pipeline = new();
        TrackingMiddleware mw1 = new();
        TrackingMiddleware mw2 = new();
        pipeline.Use(mw1);
        pipeline.Use(mw2);

        await pipeline.OnConnectedAsync(new FakeSession());

        Assert.True(mw1.ConnectedCalled);
        Assert.True(mw2.ConnectedCalled);
    }

    [Fact]
    public async Task OnDisconnected_CallsInReverseOrder()
    {
        MiddlewarePipeline pipeline = new();
        List<int> order = [];

        pipeline.Use(new OrderTrackingMiddleware(1, order));
        pipeline.Use(new OrderTrackingMiddleware(2, order));

        await pipeline.OnDisconnectedAsync(new FakeSession(), DisconnectReason.ClosedByClient);

        Assert.Equal([2, 1], order);
    }

    [Fact]
    public async Task OnError_CallsAllMiddlewares()
    {
        MiddlewarePipeline pipeline = new();
        TrackingMiddleware mw = new();
        pipeline.Use(mw);

        Exception ex = new InvalidOperationException("test");
        await pipeline.OnErrorAsync(new FakeSession(), ex);

        Assert.True(mw.ErrorCalled);
        Assert.Same(ex, mw.LastError);
    }

    [Fact]
    public async Task OnDataReceived_NoMiddleware_ReturnsOriginalData()
    {
        MiddlewarePipeline pipeline = new();
        byte[] data = [1, 2, 3];

        ReadOnlyMemory<byte> result = await pipeline.OnDataReceivedAsync(new FakeSession(), data);

        Assert.Equal(data, result.ToArray());
    }

    [Fact]
    public async Task OnDataReceived_Middleware_PassesThrough()
    {
        MiddlewarePipeline pipeline = new();
        TrackingMiddleware mw = new();
        pipeline.Use(mw);

        byte[] data = [1, 2, 3];
        ReadOnlyMemory<byte> result = await pipeline.OnDataReceivedAsync(new FakeSession(), data);

        Assert.True(mw.DataReceivedCalled);
        Assert.Equal(data, result.ToArray());
    }

    [Fact]
    public async Task OnDataReceived_SuppressMiddleware_ReturnsEmpty()
    {
        MiddlewarePipeline pipeline = new();
        pipeline.Use(new SuppressMiddleware());
        TrackingMiddleware after = new();
        pipeline.Use(after);

        ReadOnlyMemory<byte> result = await pipeline.OnDataReceivedAsync(new FakeSession(), new byte[] { 1 });

        Assert.True(result.IsEmpty);
        Assert.False(after.DataReceivedCalled); // should not be called after suppress
    }

    [Fact]
    public async Task OnDataSending_PassesDataThroughChain()
    {
        MiddlewarePipeline pipeline = new();
        TrackingMiddleware mw = new();
        pipeline.Use(mw);

        byte[] data = [1, 2, 3];
        ReadOnlyMemory<byte> result = await pipeline.OnDataSendingAsync(new FakeSession(), data);

        Assert.True(mw.DataSendingCalled);
        Assert.Equal(data, result.ToArray());
    }

    [Fact]
    public async Task OnDataSending_SuppressMiddleware_ReturnsEmpty()
    {
        MiddlewarePipeline pipeline = new();
        pipeline.Use(new SuppressMiddleware());
        TrackingMiddleware after = new();
        pipeline.Use(after);

        ReadOnlyMemory<byte> result = await pipeline.OnDataSendingAsync(new FakeSession(), new byte[] { 1 });

        Assert.True(result.IsEmpty);
        Assert.False(after.DataSendingCalled);
    }

    [Fact]
    public async Task OnDisconnected_PassesReason()
    {
        MiddlewarePipeline pipeline = new();
        TrackingMiddleware mw = new();
        pipeline.Use(mw);

        await pipeline.OnDisconnectedAsync(new FakeSession(), DisconnectReason.HeartbeatTimeout);

        Assert.True(mw.DisconnectedCalled);
        Assert.Equal(DisconnectReason.HeartbeatTimeout, mw.LastReason);
    }

    private sealed class OrderTrackingMiddleware : IConnectionMiddleware
    {
        private readonly int _id;
        private readonly List<int> _order;

        public OrderTrackingMiddleware(int id, List<int> order)
        {
            _id = id;
            _order = order;
        }

        public ValueTask OnDisconnectedAsync(ISession session, DisconnectReason reason)
        {
            _order.Add(_id);
            return ValueTask.CompletedTask;
        }
    }
}
