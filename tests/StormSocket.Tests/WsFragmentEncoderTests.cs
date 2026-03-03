using System.Buffers;
using System.IO.Pipelines;
using StormSocket.Events;
using StormSocket.WebSocket;
using Xunit;

namespace StormSocket.Tests;

public class WsFragmentEncoderTests
{
    [Fact]
    public async Task SmallPayload_WritesSingleFrame()
    {
        Pipe pipe = new();
        byte[] data = "Hello"u8.ToArray();

        WsFrameEncoder.WriteFragmented(pipe.Writer, WsOpCode.Text, data, fragmentSize: 100);
        await pipe.Writer.CompleteAsync();

        ReadResult result = await pipe.Reader.ReadAsync();
        ReadOnlySequence<byte> buffer = result.Buffer;

        Assert.True(WsFrameDecoder.TryDecodeFrame(ref buffer, out WsFrame frame));
        Assert.True(frame.Fin);
        Assert.Equal(WsOpCode.Text, frame.OpCode);
        Assert.Equal(data, frame.Payload.ToArray());

        Assert.False(WsFrameDecoder.TryDecodeFrame(ref buffer, out _)); // no more frames
    }

    [Fact]
    public async Task LargePayload_SplitsIntoCorrectFragments()
    {
        Pipe pipe = new();
        byte[] data = "HelloWorld"u8.ToArray(); // 10 bytes

        WsFrameEncoder.WriteFragmented(pipe.Writer, WsOpCode.Text, data, fragmentSize: 4);
        await pipe.Writer.CompleteAsync();

        ReadResult result = await pipe.Reader.ReadAsync();
        ReadOnlySequence<byte> buffer = result.Buffer;

        // Frame 1: Text, FIN=0, "Hell"
        Assert.True(WsFrameDecoder.TryDecodeFrame(ref buffer, out WsFrame f1));
        Assert.False(f1.Fin);
        Assert.Equal(WsOpCode.Text, f1.OpCode);
        Assert.Equal("Hell"u8.ToArray(), f1.Payload.ToArray());

        // Frame 2: Continuation, FIN=0, "oWor"
        Assert.True(WsFrameDecoder.TryDecodeFrame(ref buffer, out WsFrame f2));
        Assert.False(f2.Fin);
        Assert.Equal(WsOpCode.Continuation, f2.OpCode);
        Assert.Equal("oWor"u8.ToArray(), f2.Payload.ToArray());

        // Frame 3: Continuation, FIN=1, "ld"
        Assert.True(WsFrameDecoder.TryDecodeFrame(ref buffer, out WsFrame f3));
        Assert.True(f3.Fin);
        Assert.Equal(WsOpCode.Continuation, f3.OpCode);
        Assert.Equal("ld"u8.ToArray(), f3.Payload.ToArray());

        Assert.False(WsFrameDecoder.TryDecodeFrame(ref buffer, out _));
    }

    [Fact]
    public async Task MaskedFragmented_RoundTrip()
    {
        Pipe pipe = new();
        byte[] data = "HelloWorld"u8.ToArray();

        WsFrameEncoder.WriteMaskedFragmented(pipe.Writer, WsOpCode.Binary, data, fragmentSize: 4);
        await pipe.Writer.CompleteAsync();

        ReadResult result = await pipe.Reader.ReadAsync();
        ReadOnlySequence<byte> buffer = result.Buffer;

        // Frame 1
        Assert.True(WsFrameDecoder.TryDecodeFrame(ref buffer, out WsFrame f1));
        Assert.False(f1.Fin);
        Assert.Equal(WsOpCode.Binary, f1.OpCode);
        Assert.True(f1.Masked);
        Assert.Equal("Hell"u8.ToArray(), f1.Payload.ToArray());

        // Frame 2
        Assert.True(WsFrameDecoder.TryDecodeFrame(ref buffer, out WsFrame f2));
        Assert.False(f2.Fin);
        Assert.Equal(WsOpCode.Continuation, f2.OpCode);
        Assert.Equal("oWor"u8.ToArray(), f2.Payload.ToArray());

        // Frame 3
        Assert.True(WsFrameDecoder.TryDecodeFrame(ref buffer, out WsFrame f3));
        Assert.True(f3.Fin);
        Assert.Equal(WsOpCode.Continuation, f3.OpCode);
        Assert.Equal("ld"u8.ToArray(), f3.Payload.ToArray());
    }

    [Fact]
    public async Task EncodeDecodeAssemble_FullRoundTrip()
    {
        Pipe pipe = new();
        byte[] original = "The quick brown fox jumps over the lazy dog"u8.ToArray();

        WsFrameEncoder.WriteFragmented(pipe.Writer, WsOpCode.Text, original, fragmentSize: 10);
        await pipe.Writer.CompleteAsync();

        ReadResult result = await pipe.Reader.ReadAsync();
        ReadOnlySequence<byte> buffer = result.Buffer;

        using WsFragmentAssembler assembler = new(1024);
        WsMessage? message = null;

        while (WsFrameDecoder.TryDecodeFrame(ref buffer, out WsFrame frame))
        {
            message = assembler.TryAssemble(in frame);
        }

        Assert.NotNull(message);
        Assert.True(message.Value.IsText);
        Assert.Equal(original, message.Value.Data.ToArray());
    }
}