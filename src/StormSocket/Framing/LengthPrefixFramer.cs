using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;

namespace StormSocket.Framing;

/// <summary>
/// 4-byte big-endian length prefix framing.
/// Wire format: [4 bytes length][payload].
/// Best for binary protocols where messages can be any size.
/// Max message size: 16 MB (configurable via source).
/// </summary>
public sealed class LengthPrefixFramer : IMessageFramer
{
    private const int HeaderSize = 4;

    public bool TryReadMessage(ref ReadOnlySequence<byte> buffer, out ReadOnlyMemory<byte> message)
    {
        message = default;

        if (buffer.Length < HeaderSize)
        {
            return false;
        }

        int length;
        if (buffer.FirstSpan.Length >= HeaderSize)
        {
            length = BinaryPrimitives.ReadInt32BigEndian(buffer.FirstSpan);
        }
        else
        {
            Span<byte> header = stackalloc byte[HeaderSize];
            buffer.Slice(0, HeaderSize).CopyTo(header);
            length = BinaryPrimitives.ReadInt32BigEndian(header);
        }

        if (length < 0 || length > 16 * 1024 * 1024) // 16MB max
        {
            throw new InvalidDataException($"Invalid frame length: {length}");
        }

        long totalLength = HeaderSize + length;
        if (buffer.Length < totalLength)
        {
            return false;
        }

        ReadOnlySequence<byte> payload = buffer.Slice(HeaderSize, length);
        message = payload.ToArray();
        buffer = buffer.Slice(totalLength);
        return true;
    }

    public void WriteFrame(ReadOnlyMemory<byte> data, PipeWriter writer)
    {
        Span<byte> span = writer.GetSpan(HeaderSize + data.Length);
        BinaryPrimitives.WriteInt32BigEndian(span, data.Length);
        data.Span.CopyTo(span.Slice(HeaderSize));
        writer.Advance(HeaderSize + data.Length);
    }
}