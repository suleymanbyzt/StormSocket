using System.Buffers;
using System.IO.Pipelines;

namespace StormSocket.Framing;

/// <summary>
/// Defines how raw bytes are split into discrete messages (framing) and how messages are written back.
/// Built-in implementations: <see cref="RawFramer"/>, <see cref="LengthPrefixFramer"/>, <see cref="DelimiterFramer"/>.
/// </summary>
public interface IMessageFramer
{
    /// <summary>
    /// Tries to read a complete message from the buffer. Advances the buffer past the consumed data.
    /// </summary>
    bool TryReadMessage(ref ReadOnlySequence<byte> buffer, out ReadOnlyMemory<byte> message);

    /// <summary>
    /// Writes a framed message to the pipe writer.
    /// </summary>
    void WriteFrame(ReadOnlyMemory<byte> data, PipeWriter writer);
}