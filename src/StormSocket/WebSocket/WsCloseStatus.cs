namespace StormSocket.WebSocket;

/// <summary>WebSocket close status codes as defined in RFC 6455 Section 7.4.1.</summary>
public enum WsCloseStatus : ushort
{
    NormalClosure = 1000,
    GoingAway = 1001,
    ProtocolError = 1002,
    UnsupportedData = 1003,
    NoStatusReceived = 1005,
    AbnormalClosure = 1006,
    InvalidPayload = 1007,
    PolicyViolation = 1008,
    MessageTooBig = 1009,
    MandatoryExtension = 1010,
    InternalServerError = 1011,
}

/// <summary>Validation of Close frame bodies (RFC 6455 Sections 5.5.1 and 7.4).</summary>
internal static class WsCloseFrame
{
    /// <summary>
    /// Codes an endpoint is allowed to put in a Close frame. 1005/1006 are reserved for local
    /// reporting only, 1004 and 1012-2999 are unassigned, and 3000-4999 are registered/private use.
    /// </summary>
    public static bool IsValidOnWire(ushort code) => code switch
    {
        >= 1000 and <= 1003 => true,
        >= 1007 and <= 1011 => true,
        >= 3000 and <= 4999 => true,
        _ => false,
    };

    /// <summary>
    /// Reads and validates the body of a received Close frame.
    /// </summary>
    /// <param name="payload">The unmasked Close payload.</param>
    /// <returns>The peer's status code, or <see cref="WsCloseStatus.NoStatusReceived"/> for an empty body.</returns>
    /// <exception cref="WsProtocolException">
    /// The body is one byte long, carries a code that must not appear on the wire, or has a reason
    /// that is not valid UTF-8.
    /// </exception>
    public static WsCloseStatus ParseReceived(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
        {
            return WsCloseStatus.NoStatusReceived;
        }

        // RFC 6455 Section 5.5.1: the body is either empty or at least two bytes.
        if (payload.Length == 1)
        {
            throw new WsProtocolException(WsCloseStatus.ProtocolError, "Close frame payload must be empty or at least 2 bytes.");
        }

        ushort code = (ushort)((payload[0] << 8) | payload[1]);
        if (!IsValidOnWire(code))
        {
            throw new WsProtocolException(WsCloseStatus.ProtocolError, $"Invalid close code: {code}.");
        }

        if (payload.Length > 2 && !Utf8Validator.IsValid(payload[2..]))
        {
            throw new WsProtocolException(WsCloseStatus.InvalidPayload, "Close reason is not valid UTF-8.");
        }

        return (WsCloseStatus)code;
    }

    /// <summary>
    /// Maps a received status to the one to echo back. A peer that closed normally gets its own code
    /// mirrored; codes that exist only for local reporting are answered with a plain 1000.
    /// </summary>
    public static WsCloseStatus EchoFor(WsCloseStatus received) => received switch
    {
        WsCloseStatus.NoStatusReceived or WsCloseStatus.AbnormalClosure => WsCloseStatus.NormalClosure,
        _ => received,
    };
}