using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using StormSocket.Framing;
using Xunit;

namespace StormSocket.Tests;

public class FramerTests
{
    [Fact]
    public void RawFramer_ReturnsAllBytes()
    {
        byte[] data = [1, 2, 3, 4, 5];
        ReadOnlySequence<byte> buffer = new ReadOnlySequence<byte>(data);
        RawFramer framer = RawFramer.Instance;

        Assert.True(framer.TryReadMessage(ref buffer, out ReadOnlyMemory<byte> message));
        Assert.Equal(data, message.ToArray());
        Assert.True(buffer.IsEmpty);
    }

    [Fact]
    public void RawFramer_EmptyBuffer_ReturnsFalse()
    {
        ReadOnlySequence<byte> buffer = ReadOnlySequence<byte>.Empty;
        Assert.False(RawFramer.Instance.TryReadMessage(ref buffer, out _));
    }

    [Fact]
    public async Task LengthPrefixFramer_RoundTrip()
    {
        LengthPrefixFramer framer = new LengthPrefixFramer();
        Pipe pipe = new Pipe();

        byte[] payload = [10, 20, 30];
        framer.WriteFrame(payload, pipe.Writer);
        pipe.Writer.Complete();

        ReadResult result = await pipe.Reader.ReadAsync();
        ReadOnlySequence<byte> buffer = result.Buffer;

        Assert.True(framer.TryReadMessage(ref buffer, out ReadOnlyMemory<byte> message));
        Assert.Equal(payload, message.ToArray());
    }

    [Fact]
    public void LengthPrefixFramer_IncompleteData_ReturnsFalse()
    {
        LengthPrefixFramer framer = new LengthPrefixFramer();

        // Header says 100 bytes, but only 3 bytes of payload
        byte[] data = new byte[7];
        BinaryPrimitives.WriteInt32BigEndian(data, 100);
        ReadOnlySequence<byte> buffer = new ReadOnlySequence<byte>(data);

        Assert.False(framer.TryReadMessage(ref buffer, out _));
    }

    [Fact]
    public async Task DelimiterFramer_RoundTrip()
    {
        DelimiterFramer framer = new DelimiterFramer();
        Pipe pipe = new Pipe();

        byte[] payload = "hello"u8.ToArray();
        framer.WriteFrame(payload, pipe.Writer);
        pipe.Writer.Complete();

        ReadResult result = await pipe.Reader.ReadAsync();
        ReadOnlySequence<byte> buffer = result.Buffer;

        Assert.True(framer.TryReadMessage(ref buffer, out ReadOnlyMemory<byte> message));
        Assert.Equal(payload, message.ToArray());
    }

    [Fact]
    public void DelimiterFramer_NoDelimiter_ReturnsFalse()
    {
        DelimiterFramer framer = new DelimiterFramer();
        byte[] data = "hello"u8.ToArray();
        ReadOnlySequence<byte> buffer = new ReadOnlySequence<byte>(data);

        Assert.False(framer.TryReadMessage(ref buffer, out _));
    }

    [Fact]
    public void DelimiterFramer_MultipleMessages()
    {
        DelimiterFramer framer = new DelimiterFramer();
        byte[] data = "hello\nworld\n"u8.ToArray();
        ReadOnlySequence<byte> buffer = new ReadOnlySequence<byte>(data);

        Assert.True(framer.TryReadMessage(ref buffer, out ReadOnlyMemory<byte> msg1));
        Assert.Equal("hello"u8.ToArray(), msg1.ToArray());

        Assert.True(framer.TryReadMessage(ref buffer, out ReadOnlyMemory<byte> msg2));
        Assert.Equal("world"u8.ToArray(), msg2.ToArray());
    }
}