using System.Net;
using StormSocket.Core;
using StormSocket.Session;
using Xunit;

namespace StormSocket.Tests;

public class NetworkSessionGroupTests
{
    private sealed class FakeSession : ISession
    {
        private readonly HashSet<string> _groups = [];
        public long Id { get; }
        public ConnectionState State => ConnectionState.Connected;
        public DisconnectReason DisconnectReason => DisconnectReason.None;
        public ConnectionMetrics Metrics { get; } = new();
        public EndPoint? RemoteEndPoint => null;
        public bool IsBackpressured => false;
        public IReadOnlySet<string> Groups => _groups;
        private readonly List<byte[]> _sent = [];
        public IReadOnlyList<byte[]> Sent => _sent;

        public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();

        public FakeSession(long id) => Id = id;

        public T? Get<T>(SessionKey<T> key)
            => Items.TryGetValue(key.Name, out object? v) ? (T?)v : default;
        public void Set<T>(SessionKey<T> key, T value) => Items[key.Name] = value;

        public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            _sent.Add(data.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public void Abort() { }
        public void JoinGroup(string group) => _groups.Add(group);
        public void LeaveGroup(string group) => _groups.Remove(group);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void Add_CreatesGroupAndAddsSession()
    {
        NetworkSessionGroup groups = new();
        FakeSession session = new(1);

        groups.Add("room1", session);

        Assert.Equal(1, groups.MemberCount("room1"));
        Assert.Contains("room1", session.Groups);
    }

    [Fact]
    public void Add_MultipleSessionsToSameGroup()
    {
        NetworkSessionGroup groups = new();
        FakeSession s1 = new(1);
        FakeSession s2 = new(2);

        groups.Add("room1", s1);
        groups.Add("room1", s2);

        Assert.Equal(2, groups.MemberCount("room1"));
    }

    [Fact]
    public void Remove_RemovesSessionFromGroup()
    {
        NetworkSessionGroup groups = new();
        FakeSession session = new(1);

        groups.Add("room1", session);
        groups.Remove("room1", session);

        Assert.Equal(0, groups.MemberCount("room1"));
        Assert.DoesNotContain("room1", session.Groups);
    }

    [Fact]
    public void Remove_DeletesEmptyGroup()
    {
        NetworkSessionGroup groups = new();
        FakeSession session = new(1);

        groups.Add("room1", session);
        groups.Remove("room1", session);

        Assert.DoesNotContain("room1", groups.GroupNames);
    }

    [Fact]
    public void Remove_NonExistentGroup_DoesNotThrow()
    {
        NetworkSessionGroup groups = new();
        FakeSession session = new(1);

        groups.Remove("nonexistent", session);
    }

    [Fact]
    public void RemoveFromAll_RemovesFromMultipleGroups()
    {
        NetworkSessionGroup groups = new();
        FakeSession session = new(1);
        FakeSession other = new(2);

        groups.Add("room1", session);
        groups.Add("room2", session);
        groups.Add("room1", other);

        groups.RemoveFromAll(session);

        Assert.Equal(1, groups.MemberCount("room1")); // other still there
        Assert.Equal(0, groups.MemberCount("room2")); // empty, cleaned up
    }

    [Fact]
    public async Task BroadcastAsync_SendsToAllMembers()
    {
        NetworkSessionGroup groups = new();
        FakeSession s1 = new(1);
        FakeSession s2 = new(2);

        groups.Add("room", s1);
        groups.Add("room", s2);

        byte[] data = [1, 2, 3];
        await groups.BroadcastAsync("room", data);

        Assert.Single(s1.Sent);
        Assert.Equal(data, s1.Sent[0]);
        Assert.Single(s2.Sent);
        Assert.Equal(data, s2.Sent[0]);
    }

    [Fact]
    public async Task BroadcastAsync_ExcludesSpecifiedSession()
    {
        NetworkSessionGroup groups = new();
        FakeSession s1 = new(1);
        FakeSession s2 = new(2);

        groups.Add("room", s1);
        groups.Add("room", s2);

        await groups.BroadcastAsync("room", new byte[] { 1 }, excludeId: 1);

        Assert.Empty(s1.Sent);
        Assert.Single(s2.Sent);
    }

    [Fact]
    public async Task BroadcastAsync_NonExistentGroup_DoesNotThrow()
    {
        NetworkSessionGroup groups = new();
        await groups.BroadcastAsync("nonexistent", new byte[] { 1 });
    }

    [Fact]
    public void MemberCount_NonExistentGroup_ReturnsZero()
    {
        NetworkSessionGroup groups = new();
        Assert.Equal(0, groups.MemberCount("nonexistent"));
    }

    [Fact]
    public void GroupNames_ReturnsAllGroups()
    {
        NetworkSessionGroup groups = new();
        FakeSession session = new(1);

        groups.Add("a", session);
        groups.Add("b", session);

        Assert.Contains("a", groups.GroupNames);
        Assert.Contains("b", groups.GroupNames);
    }

    [Fact]
    public async Task BroadcastAsync_FailingSession_DoesNotAffectOthers()
    {
        NetworkSessionGroup groups = new();
        FakeSession good = new(1);
        FailingSession bad = new(2);

        groups.Add("room", good);
        groups.Add("room", bad);

        await groups.BroadcastAsync("room", new byte[] { 1 });

        Assert.Single(good.Sent);
    }

    private sealed class FailingSession : ISession
    {
        private readonly HashSet<string> _groups = [];
        public long Id { get; }
        public ConnectionState State => ConnectionState.Connected;
        public DisconnectReason DisconnectReason => DisconnectReason.None;
        public ConnectionMetrics Metrics { get; } = new();
        public EndPoint? RemoteEndPoint => null;
        public bool IsBackpressured => false;
        public IReadOnlySet<string> Groups => _groups;

        public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();

        public FailingSession(long id) => Id = id;

        public T? Get<T>(SessionKey<T> key)
            => Items.TryGetValue(key.Name, out object? v) ? (T?)v : default;
        public void Set<T>(SessionKey<T> key, T value) => Items[key.Name] = value;

        public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
            => throw new IOException("Connection lost");

        public ValueTask CloseAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public void Abort() { }
        public void JoinGroup(string group) => _groups.Add(group);
        public void LeaveGroup(string group) => _groups.Remove(group);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
