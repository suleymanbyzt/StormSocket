namespace StormSocket.Events;

/// <summary>
/// Represents a received WebSocket message with its payload and type.
/// </summary>
public readonly struct WsMessage
{
    /// <summary>Raw payload bytes of the message.</summary>
    public ReadOnlyMemory<byte> Data { get; init; }

    /// <summary>True if the message was sent as a Text frame (UTF-8), false for Binary.</summary>
    public bool IsText { get; init; }

    /// <summary>
    /// Decodes the payload as a UTF-8 string. Throws if <see cref="IsText"/> is false.
    /// </summary>
    public string Text => IsText
        ? System.Text.Encoding.UTF8.GetString(Data.Span)
        : throw new InvalidOperationException("Message is not text.");
}