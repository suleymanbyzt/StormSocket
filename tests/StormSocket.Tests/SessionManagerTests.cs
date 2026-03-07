using System.Net;
using StormSocket.Core;
using StormSocket.Session;
using Xunit;

namespace StormSocket.Tests;

public class SessionManagerTests
{
    private sealed class FakeNetworkSession : ISession
    {
        public long Id { get; init; }

        public ConnectionState State => ConnectionState.Connected;

        public DisconnectReason DisconnectReason => DisconnectReason.None;

        public ConnectionMetrics Metrics { get; } = new();

        public EndPoint? RemoteEndPoint => null;

        public bool IsBackpressured { get; set; }

        public SlowConsumerPolicy Policy { get; init; } = SlowConsumerPolicy.Wait;

        public IReadOnlySet<string> Groups => new HashSet<string>();

        public List<byte[]> SentData { get; } = [];

        public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            if (Policy != SlowConsumerPolicy.Wait && IsBackpressured)
            {
                if (Policy == SlowConsumerPolicy.Disconnect)
                {
                    _ = CloseAsync(cancellationToken);
                }

                return ValueTask.CompletedTask;
            }

            SentData.Add(data.ToArray());
            return ValueTask.CompletedTask;
        }

        public bool Closed { get; private set; }

        public ValueTask CloseAsync(CancellationToken cancellationToken = default)
        {
            Closed = true;
            return ValueTask.CompletedTask;
        }

        public void Abort() => Closed = true;

        public void JoinGroup(string group) { }

        public void LeaveGroup(string group) { }

        public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();
        public T? Get<T>(SessionKey<T> key) => Items.TryGetValue(key.Name, out object? value) ? (T?)value : default;
        public void Set<T>(SessionKey<T> key, T value) => Items[key.Name] = value;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void TryAdd_And_TryGet()
    {
        SessionManager mgr = new SessionManager();
        FakeNetworkSession networkSession = new FakeNetworkSession { Id = 1 };

        Assert.True(mgr.TryAdd(networkSession));
        Assert.Equal(1, mgr.Count);
        Assert.True(mgr.TryGet(1, out INetworkSession? found));
        Assert.Same(networkSession, found);
    }

    [Fact]
    public void TryRemove()
    {
        SessionManager mgr = new SessionManager();
        FakeNetworkSession networkSession = new FakeNetworkSession { Id = 1 };
        mgr.TryAdd(networkSession);

        Assert.True(mgr.TryRemove(1, out INetworkSession? removed));
        Assert.Same(networkSession, removed);
        Assert.Equal(0, mgr.Count);
    }

    [Fact]
    public async Task Broadcast_SendsToAll()
    {
        SessionManager mgr = new SessionManager();
        FakeNetworkSession s1 = new FakeNetworkSession { Id = 1 };
        FakeNetworkSession s2 = new FakeNetworkSession { Id = 2 };
        mgr.TryAdd(s1);
        mgr.TryAdd(s2);

        byte[] data = [42];
        await mgr.BroadcastAsync(data);

        Assert.Single(s1.SentData);
        Assert.Single(s2.SentData);
    }

    [Fact]
    public async Task Broadcast_ExcludesSession()
    {
        SessionManager mgr = new SessionManager();
        FakeNetworkSession s1 = new FakeNetworkSession { Id = 1 };
        FakeNetworkSession s2 = new FakeNetworkSession { Id = 2 };
        mgr.TryAdd(s1);
        mgr.TryAdd(s2);

        await mgr.BroadcastAsync(new byte[] { 42 }, excludeId: 1);

        Assert.Empty(s1.SentData);
        Assert.Single(s2.SentData);
    }

    [Fact]
    public async Task Broadcast_DropPolicy_SkipsBackpressuredSession()
    {
        SessionManager mgr = new SessionManager();
        FakeNetworkSession fast = new FakeNetworkSession { Id = 1, Policy = SlowConsumerPolicy.Drop };
        FakeNetworkSession slow = new FakeNetworkSession { Id = 2, Policy = SlowConsumerPolicy.Drop, IsBackpressured = true };
        mgr.TryAdd(fast);
        mgr.TryAdd(slow);

        await mgr.BroadcastAsync(new byte[] { 42 });

        Assert.Single(fast.SentData);
        Assert.Empty(slow.SentData);
    }

    [Fact]
    public async Task Broadcast_DisconnectPolicy_ClosesBackpressuredSession()
    {
        SessionManager mgr = new SessionManager();
        FakeNetworkSession fast = new FakeNetworkSession { Id = 1, Policy = SlowConsumerPolicy.Disconnect };
        FakeNetworkSession slow = new FakeNetworkSession { Id = 2, Policy = SlowConsumerPolicy.Disconnect, IsBackpressured = true };
        mgr.TryAdd(fast);
        mgr.TryAdd(slow);

        await mgr.BroadcastAsync(new byte[] { 42 });

        Assert.Single(fast.SentData);
        Assert.Empty(slow.SentData);
        Assert.True(slow.Closed);
    }

    [Fact]
    public async Task Broadcast_WaitPolicy_SendsToBackpressuredSession()
    {
        SessionManager mgr = new SessionManager();
        FakeNetworkSession fast = new FakeNetworkSession { Id = 1 };
        FakeNetworkSession slow = new FakeNetworkSession { Id = 2, IsBackpressured = true };
        mgr.TryAdd(fast);
        mgr.TryAdd(slow);

        await mgr.BroadcastAsync(new byte[] { 42 });

        Assert.Single(fast.SentData);
        Assert.Single(slow.SentData);
    }

    [Fact]
    public async Task SendAsync_DropPolicy_SkipsWhenBackpressured()
    {
        FakeNetworkSession networkSession = new FakeNetworkSession { Id = 1, Policy = SlowConsumerPolicy.Drop, IsBackpressured = true };

        await networkSession.SendAsync(new byte[] { 42 });

        Assert.Empty(networkSession.SentData);
        Assert.False(networkSession.Closed);
    }

    [Fact]
    public async Task SendAsync_DisconnectPolicy_ClosesWhenBackpressured()
    {
        FakeNetworkSession networkSession = new FakeNetworkSession { Id = 1, Policy = SlowConsumerPolicy.Disconnect, IsBackpressured = true };

        await networkSession.SendAsync(new byte[] { 42 });

        Assert.Empty(networkSession.SentData);
        Assert.True(networkSession.Closed);
    }
}