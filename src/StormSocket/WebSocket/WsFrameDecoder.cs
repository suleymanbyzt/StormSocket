using System.Buffers;
using System.Buffers.Binary;

namespace StormSocket.WebSocket;

/// <summary>
/// Decodes WebSocket frames from a ReadOnlySequence (client frames are masked).
/// </summary>
public static class WsFrameDecoder
{
    /// <param name="expectMasked">
    /// Masking expectation for the peer that sent the frame (RFC 6455 Section 5.1): <c>true</c> on a
    /// server, which must fail the connection on an unmasked client frame, <c>false</c> on a client,
    /// which must fail on a masked server frame, and <c>null</c> to skip the check.
    /// </param>
    public static bool TryDecodeFrame(ref ReadOnlySequence<byte> buffer, out WsFrame frame, int maxFrameSize = 1024 * 1024, bool allowCompressedFrames = false, bool? expectMasked = null)
        => TryDecodeFrame(ref buffer, out frame, maxFrameSize, allowCompressedFrames, expectMasked, unmaskBuffer: null);

    /// <param name="unmaskBuffer">
    /// Per-connection scratch buffer for unmasking. When supplied, the payload is written into it
    /// instead of a fresh array — the payload then stays valid only until the next frame is decoded
    /// on that connection. When null, each masked payload gets its own array.
    /// </param>
    internal static bool TryDecodeFrame(ref ReadOnlySequence<byte> buffer, out WsFrame frame, int maxFrameSize, bool allowCompressedFrames, bool? expectMasked, WsUnmaskBuffer? unmaskBuffer)
    {
        frame = default;

        if (buffer.Length < 2)
        {
            return false;
        }

        // A pipe read almost always hands over one segment, and then the header can be read where it
        // already is; copying it into scratch space is only needed when a frame straddles segments.
        Span<byte> scratch = stackalloc byte[14]; // max header: 2 + 8 + 4
        scoped ReadOnlySpan<byte> header;

        if (buffer.IsSingleSegment)
        {
            header = buffer.FirstSpan;
        }
        else
        {
            int headerLength = (int)Math.Min(buffer.Length, 14);
            buffer.Slice(0, headerLength).CopyTo(scratch);
            header = scratch[..headerLength];
        }

        bool fin = (header[0] & 0x80) != 0;
        byte rsv = (byte)((header[0] >> 4) & 0x07);
        WsOpCode opCode = (WsOpCode)(header[0] & 0x0F);
        bool masked = (header[1] & 0x80) != 0;
        long payloadLength = header[1] & 0x7F;

        // RFC 6455 Section 5.2: RSV bits must be 0 unless an extension is negotiated
        bool rsv1 = (rsv & 0x04) != 0;
        if (allowCompressedFrames)
        {
            // RSV1 is allowed for permessage-deflate (RFC 7692), RSV2/RSV3 still forbidden
            if ((rsv & 0x03) != 0)
            {
                throw new WsProtocolException(WsCloseStatus.ProtocolError, $"Non-zero RSV2/RSV3 bits: 0x{rsv:X}");
            }
        }
        else if (rsv != 0)
        {
            throw new WsProtocolException(WsCloseStatus.ProtocolError, $"Non-zero RSV bits: 0x{rsv:X}");
        }

        // RFC 6455 Section 5.2: Unknown opcodes must fail the connection
        if (opCode is not (WsOpCode.Continuation or WsOpCode.Text or WsOpCode.Binary or WsOpCode.Close or WsOpCode.Ping or WsOpCode.Pong))
        {
            throw new WsProtocolException(WsCloseStatus.ProtocolError, $"Unknown opcode: 0x{(byte)opCode:X}");
        }

        bool isControl = opCode is WsOpCode.Close or WsOpCode.Ping or WsOpCode.Pong;

        // RFC 7692 Section 6: RSV1 marks the first frame of a compressed message, so it is never
        // valid on a control frame. (Continuation frames are checked by the fragment assembler,
        // which is the layer that knows whether a message is in progress.)
        if (rsv1 && isControl)
        {
            throw new WsProtocolException(WsCloseStatus.ProtocolError, "RSV1 must not be set on a control frame.");
        }

        // RFC 6455 Section 5.1: a server must reject unmasked client frames, and a client must
        // reject masked server frames.
        if (expectMasked is bool requireMask && masked != requireMask)
        {
            throw new WsProtocolException(WsCloseStatus.ProtocolError,
                requireMask ? "Received an unmasked frame from a client." : "Received a masked frame from a server.");
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

            // RFC 6455 Section 5.2: the length must use the shortest possible encoding
            if (payloadLength < 126)
            {
                throw new WsProtocolException(WsCloseStatus.ProtocolError, $"Payload length {payloadLength} is not minimally encoded.");
            }
        }
        else if (payloadLength == 127)
        {
            if (buffer.Length < 10)
            {
                return false;
            }

            ulong extended = BinaryPrimitives.ReadUInt64BigEndian(header.Slice(2));

            // RFC 6455 Section 5.2: the most significant bit must be 0. Without this check the cast
            // below yields a negative length that slips past every size guard and blows up in Slice.
            if ((extended & 0x8000_0000_0000_0000UL) != 0)
            {
                throw new WsProtocolException(WsCloseStatus.ProtocolError, "Payload length has the most significant bit set.");
            }

            payloadLength = (long)extended;
            offset = 10;

            if (payloadLength <= ushort.MaxValue)
            {
                throw new WsProtocolException(WsCloseStatus.ProtocolError, $"Payload length {payloadLength} is not minimally encoded.");
            }
        }

        if (isControl && payloadLength > 125)
        {
            throw new WsProtocolException(WsCloseStatus.ProtocolError, $"Control frame payload too large: {payloadLength} bytes (max: 125)");
        }

        // RFC 6455 Section 5.5: control frames must not be fragmented. Checked here so it applies to
        // every read loop, including the ones that dispatch control frames without the assembler.
        if (isControl && !fin)
        {
            throw new WsProtocolException(WsCloseStatus.ProtocolError, "Control frame must not be fragmented.");
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
        ReadOnlySequence<byte> payloadSeq = buffer.Slice(offset, payloadLength);

        if (masked)
        {
            int length = (int)payloadLength;
            byte[] array = unmaskBuffer is not null ? unmaskBuffer.GetArray(length) : new byte[length];
            Span<byte> destination = array.AsSpan(0, length);

            payloadSeq.CopyTo(destination);
            WsMasking.ApplyMask(destination, header.Slice(maskOffset, 4));

            frame = new WsFrame
            {
                Fin = fin,
                Rsv1 = rsv1,
                OpCode = opCode,
                Masked = true,
                Payload = array.AsMemory(0, length),
            };
        }
        else
        {
            ReadOnlyMemory<byte> payload = payloadSeq.IsSingleSegment
                ? payloadSeq.First
                : payloadSeq.ToArray();

            frame = new WsFrame
            {
                Fin = fin,
                Rsv1 = rsv1,
                OpCode = opCode,
                Masked = false,
                Payload = payload,
            };
        }

        buffer = buffer.Slice(totalLength);
        return true;
    }
}