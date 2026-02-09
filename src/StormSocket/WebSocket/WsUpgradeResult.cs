namespace StormSocket.WebSocket;

/// <summary>
/// Result of WebSocket upgrade request validation (RFC 6455 4.2.1).
/// </summary>
public enum WsUpgradeResult
{
    /// <summary>Upgrade request is valid.</summary>
    Success,
    
    /// <summary>Request incomplete, need more data.</summary>
    Incomplete,
    
    /// <summary>Missing or invalid Upgrade header (must be "websocket").</summary>
    MissingUpgradeHeader,
    
    /// <summary>Missing or invalid Connection header (must contain "Upgrade").</summary>
    MissingConnectionHeader,
    
    /// <summary>Missing Sec-WebSocket-Key header.</summary>
    MissingKey,
    
    /// <summary>Invalid Sec-WebSocket-Version (must be 13).</summary>
    InvalidVersion,

    /// <summary>Origin header not in allowed list (CSWSH protection, RFC 6455 10.2).</summary>
    ForbiddenOrigin,
}