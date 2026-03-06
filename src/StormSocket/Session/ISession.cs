using System.Net;
using StormSocket.Core;

namespace StormSocket.Session;

// Note: DisconnectReason is set internally before OnDisconnected fires.
// Consumers read it from the session or receive it as a delegate parameter.

/// <summary>
/// Represents an active client connection. Use this to send data, check metrics, or manage groups.
/// </summary>
public interface ISession : IAsyncDisposable
{
    /// <summary>
    /// Unique session identifier (auto-incremented integer).
    /// </summary>
    long Id { get; }

    ///<summary>
    /// Current connection state (Connecting, Connected, Closing, Closed).
    /// </summary>
    ConnectionState State { get; }

    /// <summary>
    /// The reason the connection was closed. Set internally before <c>OnDisconnected</c> fires.
    /// </summary>
    DisconnectReason DisconnectReason { get; }

    /// <summary>
    /// Tracks bytes sent/received and connection uptime.
    /// </summary>
    ConnectionMetrics Metrics { get; }

    /// <summary>
    /// The remote client's IP address and port.
    /// </summary>
    EndPoint? RemoteEndPoint { get; }

    /// <summary>
    /// True when the session's send buffer is full and writes would block.
    /// Used by broadcast to detect slow consumers.
    /// </summary>
    bool IsBackpressured { get; }

    /// <summary>
    /// Set of group names this session belongs to.
    /// </summary>
    IReadOnlySet<string> Groups { get; }

    /// <summary>
    /// Sends raw bytes to the client.
    /// </summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gracefully closes the connection. For WebSocket sessions, sends a Close frame first.
    /// If the client is slow, this may take time while the Close frame is flushed.
    /// Use <see cref="Abort"/> for immediate termination.
    /// </summary>
    ValueTask CloseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Immediately terminates the connection without sending a Close frame.
    /// All pending reads and writes are cancelled, and the socket is closed.
    /// Use this for slow consumers that can't even process a graceful Close.
    /// </summary>
    void Abort();

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