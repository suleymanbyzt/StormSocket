using System.Net;
using StormSocket.Core;
using StormSocket.Middleware.RateLimiting;
using StormSocket.Session;
using Xunit;

namespace StormSocket.Tests;

public class RateLimitMiddlewareTests
{
    private sealed class FakeSession : ISession
    {
        public long Id { get; init; }
        public ConnectionState State => ConnectionState.Connected;
        public ConnectionMetrics Metrics { get; } = new();
        public EndPoint? RemoteEndPoint { get; init; }
        public bool IsBackpressured => false;
        public IReadOnlySet<string> Groups => new HashSet<string>();
        public bool Aborted { get; private set; }

        public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask CloseAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public void Abort() => Aborted = true;
        public void JoinGroup(string group) { }
        public void LeaveGroup(string group) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task AllowsMessagesWithinLimit()
    {
        RateLimitMiddleware middleware = new(new RateLimitOptions
        {
            Window = TimeSpan.FromSeconds(10),
            MaxMessages = 5,
        });

        FakeSession session = new() { Id = 1 };
        byte[] data = [1, 2, 3];

        for (int i = 0; i < 5; i++)
        {
            ReadOnlyMemory<byte> result = await middleware.OnDataReceivedAsync(session, data);
            Assert.False(result.IsEmpty);
        }
    }

    [Fact]
    public async Task DisconnectsWhenLimitExceeded()
    {
        RateLimitMiddleware middleware = new(new RateLimitOptions
        {
            Window = TimeSpan.FromSeconds(10),
            MaxMessages = 3,
            ExceededAction = RateLimitAction.Disconnect,
        });

        FakeSession session = new() { Id = 1 };
        byte[] data = [1];

        for (int i = 0; i < 3; i++)
        {
            await middleware.OnDataReceivedAsync(session, data);
        }

        ReadOnlyMemory<byte> result = await middleware.OnDataReceivedAsync(session, data);
        Assert.True(result.IsEmpty);
        Assert.True(session.Aborted);
    }

    [Fact]
    public async Task DropsMessageWhenLimitExceeded()
    {
        RateLimitMiddleware middleware = new(new RateLimitOptions
        {
            Window = TimeSpan.FromSeconds(10),
            MaxMessages = 2,
            ExceededAction = RateLimitAction.Drop,
        });

        FakeSession session = new() { Id = 1 };
        byte[] data = [1];

        await middleware.OnDataReceivedAsync(session, data);
        await middleware.OnDataReceivedAsync(session, data);

        ReadOnlyMemory<byte> result = await middleware.OnDataReceivedAsync(session, data);
        Assert.True(result.IsEmpty);
        Assert.False(session.Aborted);
    }

    [Fact]
    public async Task OnExceededEventFires()
    {
        bool eventFired = false;
        ISession? eventSession = null;

        RateLimitMiddleware middleware = new(new RateLimitOptions
        {
            Window = TimeSpan.FromSeconds(10),
            MaxMessages = 1,
            ExceededAction = RateLimitAction.Drop,
        });

        middleware.OnExceeded += session =>
        {
            eventFired = true;
            eventSession = session;
            return ValueTask.CompletedTask;
        };

        FakeSession session = new() { Id = 1 };
        await middleware.OnDataReceivedAsync(session, new byte[] { 1 });

        ReadOnlyMemory<byte> result = await middleware.OnDataReceivedAsync(session, new byte[] { 1 });
        Assert.True(result.IsEmpty);
        Assert.True(eventFired);
        Assert.Same(session, eventSession);
    }

    [Fact]
    public async Task OnExceededEventFiresBeforeDisconnect()
    {
        bool eventFired = false;
        bool wasAbortedDuringEvent = false;

        RateLimitMiddleware middleware = new(new RateLimitOptions
        {
            Window = TimeSpan.FromSeconds(10),
            MaxMessages = 1,
            ExceededAction = RateLimitAction.Disconnect,
        });

        middleware.OnExceeded += session =>
        {
            eventFired = true;
            wasAbortedDuringEvent = ((FakeSession)session).Aborted;
            return ValueTask.CompletedTask;
        };

        FakeSession session = new() { Id = 1 };
        await middleware.OnDataReceivedAsync(session, new byte[] { 1 });
        await middleware.OnDataReceivedAsync(session, new byte[] { 1 });

        Assert.True(eventFired);
        Assert.False(wasAbortedDuringEvent); // event fires BEFORE abort
        Assert.True(session.Aborted);        // then abort happens
    }

    [Fact]
    public async Task SessionScopeIsolatesSessions()
    {
        RateLimitMiddleware middleware = new(new RateLimitOptions
        {
            Window = TimeSpan.FromSeconds(10),
            MaxMessages = 2,
            Scope = RateLimitScope.Session,
            ExceededAction = RateLimitAction.Drop,
        });

        FakeSession session1 = new() { Id = 1 };
        FakeSession session2 = new() { Id = 2 };
        byte[] data = [1];

        await middleware.OnDataReceivedAsync(session1, data);
        await middleware.OnDataReceivedAsync(session1, data);

        // session1 exhausted
        ReadOnlyMemory<byte> result1 = await middleware.OnDataReceivedAsync(session1, data);
        Assert.True(result1.IsEmpty);

        // session2 still has quota
        ReadOnlyMemory<byte> result2 = await middleware.OnDataReceivedAsync(session2, data);
        Assert.False(result2.IsEmpty);
    }

    [Fact]
    public async Task IpScopeSharesLimitAcrossSessions()
    {
        RateLimitMiddleware middleware = new(new RateLimitOptions
        {
            Window = TimeSpan.FromSeconds(10),
            MaxMessages = 3,
            Scope = RateLimitScope.IpAddress,
            ExceededAction = RateLimitAction.Drop,
        });

        FakeSession session1 = new() { Id = 1, RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 5000) };
        FakeSession session2 = new() { Id = 2, RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 5001) };

        await middleware.OnConnectedAsync(session1);
        await middleware.OnConnectedAsync(session2);

        byte[] data = [1];

        await middleware.OnDataReceivedAsync(session1, data);
        await middleware.OnDataReceivedAsync(session1, data);
        await middleware.OnDataReceivedAsync(session2, data);

        // Both sessions from same IP, limit of 3 shared — now exhausted
        ReadOnlyMemory<byte> result = await middleware.OnDataReceivedAsync(session2, data);
        Assert.True(result.IsEmpty);
    }

    [Fact]
    public async Task WindowResetsAfterExpiry()
    {
        RateLimitMiddleware middleware = new(new RateLimitOptions
        {
            Window = TimeSpan.FromMilliseconds(50),
            MaxMessages = 2,
            ExceededAction = RateLimitAction.Drop,
        });

        FakeSession session = new() { Id = 1 };
        byte[] data = [1];

        await middleware.OnDataReceivedAsync(session, data);
        await middleware.OnDataReceivedAsync(session, data);

        // Exhausted
        ReadOnlyMemory<byte> blocked = await middleware.OnDataReceivedAsync(session, data);
        Assert.True(blocked.IsEmpty);

        // Wait for window to expire
        await Task.Delay(100);

        ReadOnlyMemory<byte> allowed = await middleware.OnDataReceivedAsync(session, data);
        Assert.False(allowed.IsEmpty);
    }

    [Fact]
    public async Task DisconnectCleansUpEntry()
    {
        RateLimitMiddleware middleware = new(new RateLimitOptions
        {
            Window = TimeSpan.FromSeconds(10),
            MaxMessages = 2,
            ExceededAction = RateLimitAction.Drop,
        });

        FakeSession session = new() { Id = 1 };
        byte[] data = [1];

        await middleware.OnDataReceivedAsync(session, data);
        await middleware.OnDataReceivedAsync(session, data);

        // Exhausted
        ReadOnlyMemory<byte> blocked = await middleware.OnDataReceivedAsync(session, data);
        Assert.True(blocked.IsEmpty);

        // Disconnect and reconnect (simulated with same ID)
        await middleware.OnDisconnectedAsync(session);

        // Fresh counter after reconnect
        ReadOnlyMemory<byte> allowed = await middleware.OnDataReceivedAsync(session, data);
        Assert.False(allowed.IsEmpty);
    }

    [Fact]
    public async Task IpScope_CleansUpWhenLastSessionDisconnects()
    {
        RateLimitMiddleware middleware = new(new RateLimitOptions
        {
            Window = TimeSpan.FromSeconds(10),
            MaxMessages = 2,
            Scope = RateLimitScope.IpAddress,
            ExceededAction = RateLimitAction.Drop,
        });

        IPEndPoint ep1 = new(IPAddress.Loopback, 5000);
        IPEndPoint ep2 = new(IPAddress.Loopback, 5001);
        FakeSession session1 = new() { Id = 1, RemoteEndPoint = ep1 };
        FakeSession session2 = new() { Id = 2, RemoteEndPoint = ep2 };

        await middleware.OnConnectedAsync(session1);
        await middleware.OnConnectedAsync(session2);

        byte[] data = [1];
        await middleware.OnDataReceivedAsync(session1, data);
        await middleware.OnDataReceivedAsync(session2, data);

        // Exhaust the IP limit
        ReadOnlyMemory<byte> blocked = await middleware.OnDataReceivedAsync(session1, data);
        Assert.True(blocked.IsEmpty);

        // Disconnect one session — entry stays (session2 still connected)
        await middleware.OnDisconnectedAsync(session1);

        // Still blocked because entry is shared and not reset
        ReadOnlyMemory<byte> stillBlocked = await middleware.OnDataReceivedAsync(session2, data);
        Assert.True(stillBlocked.IsEmpty);

        // Disconnect last session — entry removed
        await middleware.OnDisconnectedAsync(session2);

        // New connection gets fresh counter
        FakeSession session3 = new() { Id = 3, RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 5002) };
        await middleware.OnConnectedAsync(session3);
        ReadOnlyMemory<byte> fresh = await middleware.OnDataReceivedAsync(session3, data);
        Assert.False(fresh.IsEmpty);
    }

    [Fact]
    public void ThrowsOnInvalidOptions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimitMiddleware(new RateLimitOptions
        {
            Window = TimeSpan.Zero,
        }));

        Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimitMiddleware(new RateLimitOptions
        {
            MaxMessages = 0,
        }));
    }
}
