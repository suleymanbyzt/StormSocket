using System.Net;

namespace StormSocket.Session;

/// <summary>
/// Base interface shared by all session types (TCP, WebSocket, UDP).
/// Contains only transport-agnostic members that every session supports.
/// </summary>
public interface INetworkSession
{
    /// <summary>
    /// Unique session identifier (auto-incremented integer).
    /// </summary>
    long Id { get; }

    /// <summary>
    /// The remote client's IP address and port.
    /// </summary>
    EndPoint? RemoteEndPoint { get; }

    /// <summary>
    /// Set of group names this session belongs to.
    /// </summary>
    IReadOnlySet<string> Groups { get; }

    /// <summary>
    /// Sends raw bytes to the client.
    /// </summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds this session to a named group for broadcast.
    /// </summary>
    void JoinGroup(string group);

    /// <summary>
    /// Removes this session from a named group.
    /// </summary>
    void LeaveGroup(string group);

    /// <summary>
    /// General-purpose key/value store for attaching custom data (user ID, auth info, roles, etc.) to the session.
    /// Not thread-safe — session events are sequential per-session, so concurrent access is not expected.
    /// </summary>
    IDictionary<string, object?> Items { get; }

    /// <summary>
    /// Gets a strongly-typed value from the session's <see cref="Items"/> store.
    /// Returns <c>default(T)</c> if the key is not present.
    /// </summary>
    T? Get<T>(SessionKey<T> key);

    /// <summary>
    /// Sets a strongly-typed value in the session's <see cref="Items"/> store.
    /// </summary>
    void Set<T>(SessionKey<T> key, T value);
}
