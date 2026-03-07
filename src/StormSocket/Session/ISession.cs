using StormSocket.Core;

namespace StormSocket.Session;

// Note: DisconnectReason is set internally before OnDisconnected fires.
// Consumers read it from the session or receive it as a delegate parameter.

/// <summary>
/// Represents an active connection-oriented client session (TCP or WebSocket).
/// Extends <see cref="INetworkSession"/> with connection lifecycle members.
/// </summary>
public interface ISession : INetworkSession, IAsyncDisposable
{
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
    /// True when the session's send buffer is full and writes would block.
    /// Used by broadcast to detect slow consumers.
    /// </summary>
    bool IsBackpressured { get; }

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
}
