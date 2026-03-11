using System.Net;
using StormSocket.Core;

namespace StormSocket.Session;

/// <summary>
/// The primary session interface for all connection-oriented protocols (TCP, WebSocket).
/// Every event handler receives this interface — no casting needed for common operations.
/// <para>
/// For WebSocket-specific features (e.g. <c>SendTextAsync</c>), use <see cref="IWebSocketSession"/>.
/// WebSocket event handlers receive <see cref="IWebSocketSession"/> directly.
/// </para>
/// </summary>
public interface ISession : IAsyncDisposable
{
    /// <summary>Unique session identifier (auto-incremented).</summary>
    long Id { get; }

    /// <summary>The remote client's endpoint (IP:port or Unix socket path).</summary>
    EndPoint? RemoteEndPoint { get; }

    /// <summary>Current connection state (Connecting, Connected, Closing, Closed).</summary>
    ConnectionState State { get; }

    /// <summary>The reason the connection was closed. Set internally before <c>OnDisconnected</c> fires.</summary>
    DisconnectReason DisconnectReason { get; }

    /// <summary>Tracks bytes sent/received and connection uptime.</summary>
    ConnectionMetrics Metrics { get; }

    /// <summary>True when the session's send buffer is full and writes would block.</summary>
    bool IsBackpressured { get; }

    /// <summary>Set of group names this session belongs to.</summary>
    IReadOnlySet<string> Groups { get; }

    /// <summary>Sends raw bytes to the client.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gracefully closes the connection. For WebSocket sessions, sends a Close frame first.
    /// Use <see cref="Abort"/> for immediate termination.
    /// </summary>
    ValueTask CloseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Immediately terminates the connection without sending a Close frame.
    /// </summary>
    void Abort();

    /// <summary>Adds this session to a named group for broadcast.</summary>
    void JoinGroup(string group);

    /// <summary>Removes this session from a named group.</summary>
    void LeaveGroup(string group);

    /// <summary>
    /// General-purpose key/value store for attaching custom data to the session.
    /// </summary>
    IDictionary<string, object?> Items { get; }

    /// <summary>Gets a strongly-typed value from the session store.</summary>
    T? Get<T>(SessionKey<T> key);

    /// <summary>Sets a strongly-typed value in the session store.</summary>
    void Set<T>(SessionKey<T> key, T value);
}
