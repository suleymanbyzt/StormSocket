using System.Net;
using StormSocket.Core;
using StormSocket.Session;
using Xunit;

namespace StormSocket.Tests;

public class SessionItemsTests
{
    private sealed class FakeSession : ISession
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
        FakeSession session = new() { Id = 1 };

        session.Set(UserId, "abc123");

        Assert.Equal("abc123", session.Get(UserId));
    }

    [Fact]
    public void SetAndGet_IntValue()
    {
        FakeSession session = new() { Id = 1 };

        session.Set(Score, 42);

        Assert.Equal(42, session.Get(Score));
    }

    [Fact]
    public void SetAndGet_ComplexType()
    {
        FakeSession session = new() { Id = 1 };
        List<string> roles = ["admin", "moderator"];

        session.Set(Roles, roles);

        Assert.Same(roles, session.Get(Roles));
    }

    [Fact]
    public void Get_ReturnsDefault_WhenKeyNotSet()
    {
        FakeSession session = new() { Id = 1 };

        Assert.Null(session.Get(UserId));
        Assert.Equal(0, session.Get(Score));
    }

    [Fact]
    public void Set_OverwritesPreviousValue()
    {
        FakeSession session = new() { Id = 1 };

        session.Set(UserId, "first");
        session.Set(UserId, "second");

        Assert.Equal("second", session.Get(UserId));
    }

    [Fact]
    public void Items_Dictionary_WorksDirectly()
    {
        FakeSession session = new() { Id = 1 };

        session.Items["key"] = "value";

        Assert.Equal("value", session.Items["key"]);
    }

    [Fact]
    public void TypedAndUntypedAccess_ShareSameStore()
    {
        FakeSession session = new() { Id = 1 };

        session.Set(UserId, "abc");

        Assert.Equal("abc", session.Items["userId"]);
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
