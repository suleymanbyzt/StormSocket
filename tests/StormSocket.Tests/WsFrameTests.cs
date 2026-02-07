using System.Buffers;
using System.IO.Pipelines;
using System.Threading.Tasks;
using StormSocket.WebSocket;
using Xunit;

namespace StormSocket.Tests;

public class WsFrameTests
{
    [Fact]
    public async Task Encode_Decode_TextFrame()
    {
        Pipe pipe = new Pipe();
        byte[] text = "Hello, WebSocket!"u8.ToArray();

        WsFrameEncoder.WriteText(pipe.Writer, text);
        await pipe.Writer.CompleteAsync();

        ReadResult result = await pipe.Reader.ReadAsync();
        ReadOnlySequence<byte> buffer = result.Buffer;

        Assert.True(WsFrameDecoder.TryDecodeFrame(ref buffer, out WsFrame frame));
        Assert.True(frame.Fin);
        Assert.Equal(WsOpCode.Text, frame.OpCode);
        Assert.False(frame.Masked);
        Assert.Equal(text, frame.Payload.ToArray());
    }

    [Fact]
    public async Task Encode_Decode_BinaryFrame()
    {
        Pipe pipe = new Pipe();
        byte[] data = [0x00, 0xFF, 0x42, 0x99];

        WsFrameEncoder.WriteBinary(pipe.Writer, data);
        await pipe.Writer.CompleteAsync();

        ReadResult result = await pipe.Reader.ReadAsync();
        ReadOnlySequence<byte> buffer = result.Buffer;

        Assert.True(WsFrameDecoder.TryDecodeFrame(ref buffer, out WsFrame frame));
        Assert.True(frame.Fin);
        Assert.Equal(WsOpCode.Binary, frame.OpCode);
        Assert.Equal(data, frame.Payload.ToArray());
    }

    [Fact]
    public async Task Encode_Decode_PingPong()
    {
        Pipe pipe = new Pipe();
        WsFrameEncoder.WritePing(pipe.Writer, [1, 2, 3]);
        WsFrameEncoder.WritePong(pipe.Writer, [4, 5, 6]);
        await pipe.Writer.CompleteAsync();

        ReadResult result = await pipe.Reader.ReadAsync();
        ReadOnlySequence<byte> buffer = result.Buffer;

        Assert.True(WsFrameDecoder.TryDecodeFrame(ref buffer, out WsFrame ping));
        Assert.Equal(WsOpCode.Ping, ping.OpCode);
        Assert.Equal(new byte[] { 1, 2, 3 }, ping.Payload.ToArray());

        Assert.True(WsFrameDecoder.TryDecodeFrame(ref buffer, out WsFrame pong));
        Assert.Equal(WsOpCode.Pong, pong.OpCode);
        Assert.Equal(new byte[] { 4, 5, 6 }, pong.Payload.ToArray());
    }

    [Fact]
    public void Decode_MaskedFrame()
    {
        byte[] payload = "Hi"u8.ToArray();
        byte[] maskKey = [0x37, 0xFA, 0x21, 0x3D];

        byte[] maskedPayload = new byte[payload.Length];
        for (int i = 0; i < payload.Length; i++)
            maskedPayload[i] = (byte)(payload[i] ^ maskKey[i & 3]);

        byte[] frame = new byte[2 + 4 + payload.Length];
        frame[0] = 0x81; // FIN + Text
        frame[1] = (byte)(0x80 | payload.Length); // Masked + length
        maskKey.CopyTo(frame, 2);
        maskedPayload.CopyTo(frame, 6);

        ReadOnlySequence<byte> buffer = new ReadOnlySequence<byte>(frame);
        Assert.True(WsFrameDecoder.TryDecodeFrame(ref buffer, out WsFrame decoded));
        Assert.True(decoded.Fin);
        Assert.Equal(WsOpCode.Text, decoded.OpCode);
        Assert.True(decoded.Masked);
        Assert.Equal(payload, decoded.Payload.ToArray());
    }

    [Fact]
    public async Task Encode_Decode_CloseFrame()
    {
        Pipe pipe = new Pipe();
        WsFrameEncoder.WriteClose(pipe.Writer, WsCloseStatus.NormalClosure);
        await pipe.Writer.CompleteAsync();

        ReadResult result = await pipe.Reader.ReadAsync();
        ReadOnlySequence<byte> buffer = result.Buffer;

        Assert.True(WsFrameDecoder.TryDecodeFrame(ref buffer, out WsFrame frame));
        Assert.Equal(WsOpCode.Close, frame.OpCode);
        Assert.Equal(2, frame.Payload.Length);
    }

    [Fact]
    public void Decode_IncompleteFrame_ReturnsFalse()
    {
        ReadOnlySequence<byte> buffer = new ReadOnlySequence<byte>([0x81]);
        Assert.False(WsFrameDecoder.TryDecodeFrame(ref buffer, out _));
    }

    [Fact]
    public void Decode_ReservedOpcode_ThrowsProtocolError()
    {
        byte[] frame = [0x83, 0x00]; // FIN + opcode 0x3, no payload
        ReadOnlySequence<byte> buffer = new ReadOnlySequence<byte>(frame);

        WsProtocolException ex = Assert.Throws<WsProtocolException>(() => WsFrameDecoder.TryDecodeFrame(ref buffer, out _));
        Assert.Equal(WsCloseStatus.ProtocolError, ex.CloseStatus);
    }

    [Fact]
    public void Decode_NonZeroRsvBits_ThrowsProtocolError()
    {
        byte[] frame = [0xC1, 0x00];
        ReadOnlySequence<byte> buffer = new ReadOnlySequence<byte>(frame);

        WsProtocolException ex = Assert.Throws<WsProtocolException>(() =>
            WsFrameDecoder.TryDecodeFrame(ref buffer, out _));
        Assert.Equal(WsCloseStatus.ProtocolError, ex.CloseStatus);
    }

    [Fact]
    public void Decode_ControlFramePayloadTooLarge_ThrowsProtocolError()
    {
        byte[] frame = new byte[2 + 2 + 126];
        frame[0] = 0x89; // FIN + Ping
        frame[1] = 126;
        frame[2] = 0;
        frame[3] = 126;
        ReadOnlySequence<byte> buffer = new ReadOnlySequence<byte>(frame);

        WsProtocolException ex = Assert.Throws<WsProtocolException>(() => WsFrameDecoder.TryDecodeFrame(ref buffer, out _));
        Assert.Equal(WsCloseStatus.ProtocolError, ex.CloseStatus);
    }
}