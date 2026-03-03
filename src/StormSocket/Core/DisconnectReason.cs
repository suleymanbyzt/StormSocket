namespace StormSocket.Core;

/// <summary>
/// Indicates why a connection was closed.
/// Passed to <c>OnDisconnected</c> so callers can distinguish normal from abnormal disconnects.
/// </summary>
public enum DisconnectReason
{
    /// <summary>Reason was not determined (should not appear in practice).</summary>
    None,

    /// <summary>The remote peer initiated a graceful close (TCP FIN or WebSocket Close frame).</summary>
    ClosedByClient,

    /// <summary>The local side called <c>CloseAsync()</c>.</summary>
    ClosedByServer,

    /// <summary>The local side called <c>Abort()</c> for immediate termination.</summary>
    Aborted,

    /// <summary>A WebSocket protocol violation was detected (RFC 6455).</summary>
    ProtocolError,

    /// <summary>A socket or I/O-level error occurred. Check <c>OnError</c> for the exception details.</summary>
    TransportError,

    /// <summary>The remote peer stopped responding to heartbeat pings.</summary>
    HeartbeatTimeout,

    /// <summary>The WebSocket upgrade handshake was not completed in time.</summary>
    HandshakeTimeout,

    /// <summary>The session was disconnected because it could not keep up with outgoing data (slow consumer).</summary>
    SlowConsumer,

    /// <summary>The server is shutting down (RFC 6455 status 1001 Going Away).</summary>
    GoingAway,

    /// <summary>The session exceeded the configured rate limit.</summary>
    RateLimited,
}