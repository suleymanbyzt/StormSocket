using System.Buffers;
using System.IO.Pipelines;

namespace StormSocket.Framing;

/// <summary>
/// Splits messages on a single-byte delimiter (default: newline <c>'\n'</c>).
/// The delimiter is stripped from the delivered message and appended on write.
/// Good for line-based text protocols (chat, telnet-style, RESP, etc.).
/// </summary>
public sealed class DelimiterFramer : IMessageFramer
{
    private readonly byte _delimiter;

    /// <param name="delimiter">The byte that marks the end of each message. Default: <c>0x0A</c> (<c>'\n'</c>).</param>
    public DelimiterFramer(byte delimiter = (byte)'\n')
    {
        _delimiter = delimiter;
    }

    public bool TryReadMessage(ref ReadOnlySequence<byte> buffer, out ReadOnlyMemory<byte> message)
    {
        message = default;

        SequencePosition? position = buffer.PositionOf(_delimiter);
        if (position is null)
        {
            return false;
        }

        ReadOnlySequence<byte> slice = buffer.Slice(0, position.Value);
        message = slice.IsSingleSegment ? slice.First : slice.ToArray();
        buffer = buffer.Slice(buffer.GetPosition(1, position.Value)); // skip delimiter
        return true;
    }

    public void WriteFrame(ReadOnlyMemory<byte> data, PipeWriter writer)
    {
        Span<byte> span = writer.GetSpan(data.Length + 1);
        data.Span.CopyTo(span);
        span[data.Length] = _delimiter;
        writer.Advance(data.Length + 1);
    }
}