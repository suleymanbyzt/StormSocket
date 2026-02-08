using System.Buffers;
using System.IO.Pipelines;

namespace StormSocket.Framing;

/// <summary>
/// No framing: delivers all available bytes as a single chunk.
/// This is the default when no <see cref="IMessageFramer"/> is specified.
/// Use when you handle your own message boundaries or just need raw TCP streams.
/// </summary>
public sealed class RawFramer : IMessageFramer
{
    /// <summary>Shared singleton instance.</summary>
    public static readonly RawFramer Instance = new();

    public bool TryReadMessage(ref ReadOnlySequence<byte> buffer, out ReadOnlyMemory<byte> message)
    {
        if (buffer.IsEmpty)
        {
            message = default;
            return false;
        }

        message = buffer.IsSingleSegment ? buffer.First : buffer.ToArray();
        buffer = buffer.Slice(buffer.End);
        return true;
    }

    public void WriteFrame(ReadOnlyMemory<byte> data, PipeWriter writer)
    {
        Span<byte> span = writer.GetSpan(data.Length);
        data.Span.CopyTo(span);
        writer.Advance(data.Length);
    }
}