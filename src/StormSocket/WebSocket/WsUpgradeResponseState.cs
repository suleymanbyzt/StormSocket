namespace StormSocket.WebSocket;

/// <summary>Outcome of parsing a server's WebSocket handshake response.</summary>
internal enum WsUpgradeResponseState
{
    /// <summary>The header block is not complete yet — read more and try again.</summary>
    Incomplete,

    /// <summary>A valid <c>101 Switching Protocols</c> with a matching <c>Sec-WebSocket-Accept</c>.</summary>
    Accepted,

    /// <summary>The server answered with something other than 101, so the connection will not upgrade.</summary>
    Rejected,

    /// <summary>A 101 whose <c>Sec-WebSocket-Accept</c> does not match the key that was sent (RFC 6455 Section 4.1).</summary>
    InvalidAcceptKey,
}
