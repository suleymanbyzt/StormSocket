using System.Net;
using StormSocket.Core;
using StormSocket.Session;
using Xunit;

namespace StormSocket.Tests;

public class SessionItemsTests
{
    private sealed class FakeNetworkSession : ISession
    {
        public long Id { get; init; }
        public ConnectionState State => ConnectionState.Connected;
        public DisconnectReason DisconnectReason => DisconnectReason.None;
        public ConnectionMetrics Metrics { get; } = new();
        public EndPoint? RemoteEndPoint => null;
        public bool IsBackpressured => false;
        public IReadOnlySet<string> Groups => new HashSet<string>();
        public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();

        public T? Get<T>(SessionKey<T> key) =>
            Items.TryGetValue(key.Name, out object? value) ? (T?)value : default;

        public void Set<T>(SessionKey<T> key, T value) =>
            Items[key.Name] = value;

        public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
        public ValueTask CloseAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
        public void Abort() { }
        public void JoinGroup(string group) { }
        public void LeaveGroup(string group) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static readonly SessionKey<string> UserId = new("userId");
    private static readonly SessionKey<int> Score = new("score");
    private static readonly SessionKey<List<string>> Roles = new("roles");

    [Fact]
    public void SetAndGet_StringValue()
    {
        FakeNetworkSession networkSession = new() { Id = 1 };

        networkSession.Set(UserId, "abc123");

        Assert.Equal("abc123", networkSession.Get(UserId));
    }

    [Fact]
    public void SetAndGet_IntValue()
    {
        FakeNetworkSession networkSession = new() { Id = 1 };

        networkSession.Set(Score, 42);

        Assert.Equal(42, networkSession.Get(Score));
    }

    [Fact]
    public void SetAndGet_ComplexType()
    {
        FakeNetworkSession networkSession = new() { Id = 1 };
        List<string> roles = ["admin", "moderator"];

        networkSession.Set(Roles, roles);

        Assert.Same(roles, networkSession.Get(Roles));
    }

    [Fact]
    public void Get_ReturnsDefault_WhenKeyNotSet()
    {
        FakeNetworkSession networkSession = new() { Id = 1 };

        Assert.Null(networkSession.Get(UserId));
        Assert.Equal(0, networkSession.Get(Score));
    }

    [Fact]
    public void Set_OverwritesPreviousValue()
    {
        FakeNetworkSession networkSession = new() { Id = 1 };

        networkSession.Set(UserId, "first");
        networkSession.Set(UserId, "second");

        Assert.Equal("second", networkSession.Get(UserId));
    }

    [Fact]
    public void Items_Dictionary_WorksDirectly()
    {
        FakeNetworkSession networkSession = new() { Id = 1 };

        networkSession.Items["key"] = "value";

        Assert.Equal("value", networkSession.Items["key"]);
    }

    [Fact]
    public void TypedAndUntypedAccess_ShareSameStore()
    {
        FakeNetworkSession networkSession = new() { Id = 1 };

        networkSession.Set(UserId, "abc");

        Assert.Equal("abc", networkSession.Items["userId"]);
    }

    [Fact]
    public void SessionKey_Name_IsPreserved()
    {
        SessionKey<string> key = new("myKey");

        Assert.Equal("myKey", key.Name);
    }

    [Fact]
    public void SessionKey_ThrowsOnNullName()
    {
        Assert.Throws<ArgumentNullException>(() => new SessionKey<string>(null!));
    }
}
