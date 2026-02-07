using System.Buffers;
using System.Buffers.Binary;

namespace StormSocket.WebSocket;

/// <summary>
/// Decodes WebSocket frames from a ReadOnlySequence (client frames are masked).
/// </summary>
public static class WsFrameDecoder
{
    public static bool TryDecodeFrame(ref ReadOnlySequence<byte> buffer, out WsFrame frame, int maxFrameSize = 1024 * 1024)
    {
        frame = default;

        if (buffer.Length < 2)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[14]; // max header: 2 + 8 + 4
        int headerLength = (int)Math.Min(buffer.Length, 14);
        buffer.Slice(0, headerLength).CopyTo(header);

        bool fin = (header[0] & 0x80) != 0;
        byte rsv = (byte)((header[0] >> 4) & 0x07);
        WsOpCode opCode = (WsOpCode)(header[0] & 0x0F);
        bool masked = (header[1] & 0x80) != 0;
        long payloadLength = header[1] & 0x7F;

        // RFC 6455 Section 5.2: RSV bits must be 0 unless an extension is negotiated
        if (rsv != 0)
        {
            throw new WsProtocolException(WsCloseStatus.ProtocolError, $"Non-zero RSV bits: 0x{rsv:X}");
        }

        // RFC 6455 Section 5.2: Unknown opcodes must fail the connection
        if (opCode is not (WsOpCode.Continuation or WsOpCode.Text or WsOpCode.Binary or WsOpCode.Close or WsOpCode.Ping or WsOpCode.Pong))
        {
            throw new WsProtocolException(WsCloseStatus.ProtocolError, $"Unknown opcode: 0x{(byte)opCode:X}");
        }

        int offset = 2;

        if (payloadLength == 126)
        {
            if (buffer.Length < 4)
            {
                return false;
            }
            
            payloadLength = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(2));
            offset = 4;
        }
        else if (payloadLength == 127)
        {
            if (buffer.Length < 10)
            {
                return false;
            }
            
            payloadLength = (long)BinaryPrimitives.ReadUInt64BigEndian(header.Slice(2));
            offset = 10;
        }

        bool isControl = opCode is WsOpCode.Close or WsOpCode.Ping or WsOpCode.Pong;
        if (isControl && payloadLength > 125)
        {
            throw new WsProtocolException(WsCloseStatus.ProtocolError, $"Control frame payload too large: {payloadLength} bytes (max: 125)");
        }

        if (payloadLength > maxFrameSize)
        {
            throw new WsProtocolException(WsCloseStatus.MessageTooBig, $"WebSocket frame too large: {payloadLength} bytes (max: {maxFrameSize})");
        }

        int maskOffset = offset;
        if (masked)
        {
            offset += 4;
        }

        long totalLength = offset + payloadLength;
        if (buffer.Length < totalLength)
        {
            return false;
        }

        // extract payload
        byte[] payload = new byte[payloadLength];
        buffer.Slice(offset, payloadLength).CopyTo(payload);

        // unmask if needed
        if (masked)
        {
            Span<byte> maskKey = header.Slice(maskOffset, 4);
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] ^= maskKey[i & 3];
            }
        }

        frame = new WsFrame
        {
            Fin = fin,
            OpCode = opCode,
            Masked = masked,
            Payload = payload,
        };

        buffer = buffer.Slice(totalLength);
        return true;
    }
}