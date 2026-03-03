using StormSocket.Events;
using StormSocket.WebSocket;
using Xunit;

namespace StormSocket.Tests;

public class WsFragmentAssemblerTests
{
    [Fact]
    public void SingleUnfragmentedText_ReturnsImmediately()
    {
        using WsFragmentAssembler assembler = new(1024);

        WsMessage? result = assembler.TryAssemble(new WsFrame
        {
            Fin = true, OpCode = WsOpCode.Text, Payload = "Hello"u8.ToArray(),
        });

        Assert.NotNull(result);
        Assert.True(result.Value.IsText);
        Assert.Equal("Hello"u8.ToArray(), result.Value.Data.ToArray());
        Assert.False(assembler.IsAssembling);
    }

    [Fact]
    public void SingleUnfragmentedBinary_ReturnsImmediately()
    {
        using WsFragmentAssembler assembler = new(1024);
        byte[] data = [0x01, 0x02, 0x03];

        WsMessage? result = assembler.TryAssemble(new WsFrame
        {
            Fin = true, OpCode = WsOpCode.Binary, Payload = data,
        });

        Assert.NotNull(result);
        Assert.False(result.Value.IsText);
        Assert.Equal(data, result.Value.Data.ToArray());
    }

    [Fact]
    public void TwoFragmentText_AssemblesCorrectly()
    {
        using WsFragmentAssembler assembler = new(1024);

        WsMessage? r1 = assembler.TryAssemble(new WsFrame
        {
            Fin = false, OpCode = WsOpCode.Text, Payload = "Hello "u8.ToArray(),
        });
        Assert.Null(r1);
        Assert.True(assembler.IsAssembling);

        WsMessage? r2 = assembler.TryAssemble(new WsFrame
        {
            Fin = true, OpCode = WsOpCode.Continuation, Payload = "World"u8.ToArray(),
        });

        Assert.NotNull(r2);
        Assert.True(r2.Value.IsText);
        Assert.Equal("Hello World"u8.ToArray(), r2.Value.Data.ToArray());
        Assert.False(assembler.IsAssembling);
    }

    [Fact]
    public void MultiFragmentBinary_AssemblesCorrectly()
    {
        using WsFragmentAssembler assembler = new(1024);

        Assert.Null(assembler.TryAssemble(new WsFrame
        {
            Fin = false, OpCode = WsOpCode.Binary, Payload = new byte[] { 1, 2 },
        }));

        Assert.Null(assembler.TryAssemble(new WsFrame
        {
            Fin = false, OpCode = WsOpCode.Continuation, Payload = new byte[] { 3, 4 },
        }));

        Assert.Null(assembler.TryAssemble(new WsFrame
        {
            Fin = false, OpCode = WsOpCode.Continuation, Payload = new byte[] { 5 },
        }));

        WsMessage? result = assembler.TryAssemble(new WsFrame
        {
            Fin = true, OpCode = WsOpCode.Continuation, Payload = new byte[] { 6, 7 },
        });

        Assert.NotNull(result);
        Assert.False(result.Value.IsText);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7 }, result.Value.Data.ToArray());
    }

    [Fact]
    public void ControlFrameDuringFragment_PassesThrough()
    {
        using WsFragmentAssembler assembler = new(1024);

        // Start fragment
        Assert.Null(assembler.TryAssemble(new WsFrame
        {
            Fin = false, OpCode = WsOpCode.Text, Payload = "Hello "u8.ToArray(),
        }));

        // Ping in the middle — returns null (caller handles control frames)
        WsMessage? pingResult = assembler.TryAssemble(new WsFrame
        {
            Fin = true, OpCode = WsOpCode.Ping, Payload = Array.Empty<byte>(),
        });
        Assert.Null(pingResult);
        Assert.True(assembler.IsAssembling); // still assembling

        // Final fragment
        WsMessage? result = assembler.TryAssemble(new WsFrame
        {
            Fin = true, OpCode = WsOpCode.Continuation, Payload = "World"u8.ToArray(),
        });

        Assert.NotNull(result);
        Assert.Equal("Hello World"u8.ToArray(), result.Value.Data.ToArray());
    }

    [Fact]
    public void ContinuationWithoutDataFrame_ThrowsProtocolError()
    {
        using WsFragmentAssembler assembler = new(1024);

        WsProtocolException ex = Assert.Throws<WsProtocolException>(() =>
            assembler.TryAssemble(new WsFrame
            {
                Fin = true, OpCode = WsOpCode.Continuation, Payload = "data"u8.ToArray(),
            }));

        Assert.Equal(WsCloseStatus.ProtocolError, ex.CloseStatus);
    }

    [Fact]
    public void DataFrameDuringAssembly_ThrowsProtocolError()
    {
        using WsFragmentAssembler assembler = new(1024);

        assembler.TryAssemble(new WsFrame
        {
            Fin = false, OpCode = WsOpCode.Text, Payload = "start"u8.ToArray(),
        });

        WsProtocolException ex = Assert.Throws<WsProtocolException>(() =>
            assembler.TryAssemble(new WsFrame
            {
                Fin = true, OpCode = WsOpCode.Text, Payload = "new"u8.ToArray(),
            }));

        Assert.Equal(WsCloseStatus.ProtocolError, ex.CloseStatus);
    }

    [Fact]
    public void FragmentedControlFrame_ThrowsProtocolError()
    {
        using WsFragmentAssembler assembler = new(1024);

        WsProtocolException ex = Assert.Throws<WsProtocolException>(() =>
            assembler.TryAssemble(new WsFrame
            {
                Fin = false, OpCode = WsOpCode.Ping, Payload = Array.Empty<byte>(),
            }));

        Assert.Equal(WsCloseStatus.ProtocolError, ex.CloseStatus);
    }

    [Fact]
    public void MaxMessageSizeExceeded_ThrowsMessageTooBig()
    {
        using WsFragmentAssembler assembler = new(maxMessageSize: 10);

        assembler.TryAssemble(new WsFrame
        {
            Fin = false, OpCode = WsOpCode.Text, Payload = new byte[8],
        });

        WsProtocolException ex = Assert.Throws<WsProtocolException>(() =>
            assembler.TryAssemble(new WsFrame
            {
                Fin = true, OpCode = WsOpCode.Continuation, Payload = new byte[5],
            }));

        Assert.Equal(WsCloseStatus.MessageTooBig, ex.CloseStatus);
        Assert.False(assembler.IsAssembling); // state reset after error
    }

    [Fact]
    public void EmptyFirstFragment_WorksCorrectly()
    {
        using WsFragmentAssembler assembler = new(1024);

        Assert.Null(assembler.TryAssemble(new WsFrame
        {
            Fin = false, OpCode = WsOpCode.Text, Payload = Array.Empty<byte>(),
        }));

        WsMessage? result = assembler.TryAssemble(new WsFrame
        {
            Fin = true, OpCode = WsOpCode.Continuation, Payload = "data"u8.ToArray(),
        });

        Assert.NotNull(result);
        Assert.Equal("data"u8.ToArray(), result.Value.Data.ToArray());
    }

    [Fact]
    public void SequentialFragmentedMessages_BothAssembleCorrectly()
    {
        using WsFragmentAssembler assembler = new(1024);

        // First fragmented message
        assembler.TryAssemble(new WsFrame
        {
            Fin = false, OpCode = WsOpCode.Text, Payload = "AB"u8.ToArray(),
        });
        WsMessage? msg1 = assembler.TryAssemble(new WsFrame
        {
            Fin = true, OpCode = WsOpCode.Continuation, Payload = "CD"u8.ToArray(),
        });

        Assert.NotNull(msg1);
        Assert.Equal("ABCD"u8.ToArray(), msg1.Value.Data.ToArray());

        // Second fragmented message
        assembler.TryAssemble(new WsFrame
        {
            Fin = false, OpCode = WsOpCode.Binary, Payload = new byte[] { 1, 2 },
        });
        WsMessage? msg2 = assembler.TryAssemble(new WsFrame
        {
            Fin = true, OpCode = WsOpCode.Continuation, Payload = new byte[] { 3, 4 },
        });

        Assert.NotNull(msg2);
        Assert.False(msg2.Value.IsText);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, msg2.Value.Data.ToArray());
    }
}