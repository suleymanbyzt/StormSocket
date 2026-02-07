using System.Buffers.Binary;
using System.IO.Pipelines;

namespace StormSocket.WebSocket;

/// <summary>
/// Encodes WebSocket frames to a PipeWriter (server-side: no masking).
/// </summary>
public static class WsFrameEncoder
{
    public static void WriteFrame(PipeWriter writer, WsOpCode opCode, ReadOnlySpan<byte> payload, bool fin = true)
    {
        int headerSize = 2;
        int payloadLength = payload.Length;

        if (payloadLength > 65535)
        {
            headerSize += 8;
        }
        else if (payloadLength > 125)
        {
            headerSize += 2;
        }

        Span<byte> span = writer.GetSpan(headerSize + payloadLength);

        // first byte: FIN + opcode
        span[0] = (byte)((fin ? 0x80 : 0) | (int)opCode);

        // second byte: payload length (no mask for server frames)
        if (payloadLength <= 125)
        {
            span[1] = (byte)payloadLength;
        }
        else if (payloadLength <= 65535)
        {
            span[1] = 126;
            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(2), (ushort)payloadLength);
        }
        else
        {
            span[1] = 127;
            BinaryPrimitives.WriteUInt64BigEndian(span.Slice(2), (ulong)payloadLength);
        }

        payload.CopyTo(span.Slice(headerSize));
        writer.Advance(headerSize + payloadLength);
    }

    public static void WriteText(PipeWriter writer, ReadOnlySpan<byte> utf8Text) => WriteFrame(writer, WsOpCode.Text, utf8Text);

    public static void WriteBinary(PipeWriter writer, ReadOnlySpan<byte> data) => WriteFrame(writer, WsOpCode.Binary, data);

    public static void WritePing(PipeWriter writer, ReadOnlySpan<byte> payload = default) => WriteFrame(writer, WsOpCode.Ping, payload);

    public static void WritePong(PipeWriter writer, ReadOnlySpan<byte> payload = default) => WriteFrame(writer, WsOpCode.Pong, payload);

    public static void WriteClose(PipeWriter writer, WsCloseStatus status = WsCloseStatus.NormalClosure)
    {
        Span<byte> payload = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)status);
        WriteFrame(writer, WsOpCode.Close, payload);
    }
}