namespace StormSocket.WebSocket;

/// <summary>
/// Represents a decoded WebSocket frame. Produced by <see cref="WsFrameDecoder"/>.
/// </summary>
public readonly struct WsFrame
{
    /// <summary>True if this is the final fragment of a message.</summary>
    public bool Fin { get; init; }

    /// <summary>The frame type (Text, Binary, Ping, Pong, Close, Continuation).</summary>
    public WsOpCode OpCode { get; init; }

    /// <summary>True if the payload was masked (client-to-server frames are always masked per RFC 6455).</summary>
    public bool Masked { get; init; }

    /// <summary>The unmasked payload data.</summary>
    public ReadOnlyMemory<byte> Payload { get; init; }

    /// <summary>True for control frames (Close, Ping, Pong).</summary>
    public bool IsControl => OpCode is WsOpCode.Close or WsOpCode.Ping or WsOpCode.Pong;

    /// <summary>True if opcode is Text.</summary>
    public bool IsText => OpCode == WsOpCode.Text;

    /// <summary>True if opcode is Binary.</summary>
    public bool IsBinary => OpCode == WsOpCode.Binary;
}