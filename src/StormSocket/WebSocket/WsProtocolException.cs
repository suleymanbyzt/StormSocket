namespace StormSocket.WebSocket;

/// <summary>
/// Thrown when a WebSocket protocol violation is detected.
/// Contains the appropriate <see cref="WsCloseStatus"/> to send before closing.
/// </summary>
public sealed class WsProtocolException : Exception
{
    /// <summary>The RFC 6455 close status code to send to the remote peer.</summary>
    public WsCloseStatus CloseStatus { get; }

    public WsProtocolException(WsCloseStatus closeStatus, string message) : base(message)
    {
        CloseStatus = closeStatus;
    }
}