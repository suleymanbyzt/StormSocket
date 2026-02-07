using System.Net;
using System.Threading.Tasks;
using StormSocket.Core;
using StormSocket.Session;
using Xunit;

namespace StormSocket.Tests;

public class SessionManagerTests
{
    private sealed class FakeSession : ISession
    {
        public long Id { get; init; }
        
        public ConnectionState State => ConnectionState.Connected;
        
        public ConnectionMetrics Metrics { get; } = new();

        public EndPoint? RemoteEndPoint => null;

        public IReadOnlySet<string> Groups => new HashSet<string>();
        
        public List<byte[]> SentData { get; } = [];

        public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            SentData.Add(data.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        
        public void JoinGroup(string group) { }
        
        public void LeaveGroup(string group) { }
        
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void TryAdd_And_TryGet()
    {
        SessionManager mgr = new SessionManager();
        FakeSession session = new FakeSession { Id = 1 };

        Assert.True(mgr.TryAdd(session));
        Assert.Equal(1, mgr.Count);
        Assert.True(mgr.TryGet(1, out ISession? found));
        Assert.Same(session, found);
    }

    [Fact]
    public void TryRemove()
    {
        SessionManager mgr = new SessionManager();
        FakeSession session = new FakeSession { Id = 1 };
        mgr.TryAdd(session);

        Assert.True(mgr.TryRemove(1, out ISession? removed));
        Assert.Same(session, removed);
        Assert.Equal(0, mgr.Count);
    }

    [Fact]
    public async Task Broadcast_SendsToAll()
    {
        SessionManager mgr = new SessionManager();
        FakeSession s1 = new FakeSession { Id = 1 };
        FakeSession s2 = new FakeSession { Id = 2 };
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
        FakeSession s1 = new FakeSession { Id = 1 };
        FakeSession s2 = new FakeSession { Id = 2 };
        mgr.TryAdd(s1);
        mgr.TryAdd(s2);

        await mgr.BroadcastAsync(new byte[] { 42 }, excludeId: 1);

        Assert.Empty(s1.SentData);
        Assert.Single(s2.SentData);
    }
}