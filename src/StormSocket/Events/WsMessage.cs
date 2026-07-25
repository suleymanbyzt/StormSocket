namespace StormSocket.Events;

/// <summary>
/// Represents a received WebSocket message with its payload and type.
/// </summary>
public readonly struct WsMessage
{
    /// <summary>Raw payload bytes of the message.</summary>
    /// <remarks>
    /// Valid only for the duration of the handler. The payload points into a buffer the connection
    /// reuses for the next frame, so anything that outlives the handler — a queue, a field, a
    /// captured closure — must copy it first (<c>Data.ToArray()</c>).
    /// </remarks>
    public ReadOnlyMemory<byte> Data { get; init; }

    /// <summary>True if the message was sent as a Text frame (UTF-8), false for Binary.</summary>
    public bool IsText { get; init; }

    /// <summary>True if the message was compressed (RSV1 set on first frame). Used internally for decompression.</summary>
    internal bool Compressed { get; init; }

    /// <summary>
    /// Decodes the payload as a UTF-8 string. Throws if <see cref="IsText"/> is false.
    /// </summary>
    /// <remarks>
    /// The bytes were already validated as well-formed UTF-8 when the frame arrived (RFC 6455
    /// Section 8.1), so this cannot silently produce replacement characters.
    /// </remarks>
    public string Text => IsText
        ? System.Text.Encoding.UTF8.GetString(Data.Span)
        : throw new InvalidOperationException("Message is not text.");
}