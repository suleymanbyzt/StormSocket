using System.IO.Compression;
using StormSocket.WebSocket;
using Xunit;

namespace StormSocket.Tests;

public class WsPerMessageDeflateTests
{
    [Fact]
    public void CompressDecompress_RoundTrip()
    {
        using WsPerMessageDeflate deflate = new(
            CompressionLevel.Fastest, minMessageSize: 0,
            compressNoContextTakeover: true, decompressNoContextTakeover: true);

        byte[] original = "Hello, permessage-deflate!"u8.ToArray();
        byte[] compressed = deflate.Compress(original);
        byte[] decompressed = deflate.Decompress(compressed);

        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void CompressDecompress_LargePayload()
    {
        using WsPerMessageDeflate deflate = new(
            CompressionLevel.Fastest, minMessageSize: 0,
            compressNoContextTakeover: true, decompressNoContextTakeover: true);

        // Repetitive data compresses well
        byte[] original = new byte[10_000];
        for (int i = 0; i < original.Length; i++)
            original[i] = (byte)(i % 26 + 'a');

        byte[] compressed = deflate.Compress(original);
        byte[] decompressed = deflate.Decompress(compressed);

        Assert.Equal(original, decompressed);
        Assert.True(compressed.Length < original.Length, "Compressed should be smaller for repetitive data");
    }

    [Fact]
    public void CompressDecompress_EmptyPayload()
    {
        using WsPerMessageDeflate deflate = new(
            CompressionLevel.Fastest, minMessageSize: 0,
            compressNoContextTakeover: true, decompressNoContextTakeover: true);

        byte[] compressed = deflate.Compress(ReadOnlySpan<byte>.Empty);
        byte[] decompressed = deflate.Decompress(compressed);

        Assert.Empty(decompressed);
    }

    [Fact]
    public void ShouldCompress_RespectsMinMessageSize()
    {
        using WsPerMessageDeflate deflate = new(
            CompressionLevel.Fastest, minMessageSize: 128,
            compressNoContextTakeover: true, decompressNoContextTakeover: true);

        Assert.False(deflate.ShouldCompress(50));
        Assert.False(deflate.ShouldCompress(127));
        Assert.True(deflate.ShouldCompress(128));
        Assert.True(deflate.ShouldCompress(1000));
    }

    [Fact]
    public void TryNegotiate_DisabledServerReturnsNull()
    {
        WsCompressionOptions options = new() { Enabled = false };
        (WsPerMessageDeflate? deflate, string? response) =
            WsPerMessageDeflate.TryNegotiate("permessage-deflate", options);

        Assert.Null(deflate);
        Assert.Null(response);
    }

    [Fact]
    public void TryNegotiate_NoClientOfferReturnsNull()
    {
        WsCompressionOptions options = new() { Enabled = true };
        (WsPerMessageDeflate? deflate, string? response) =
            WsPerMessageDeflate.TryNegotiate(null, options);

        Assert.Null(deflate);
        Assert.Null(response);
    }

    [Fact]
    public void TryNegotiate_SuccessfulNegotiation()
    {
        WsCompressionOptions options = new()
        {
            Enabled = true,
            ServerNoContextTakeover = true,
            ClientNoContextTakeover = true,
        };

        (WsPerMessageDeflate? deflate, string? response) =
            WsPerMessageDeflate.TryNegotiate("permessage-deflate; client_no_context_takeover", options);

        Assert.NotNull(deflate);
        Assert.NotNull(response);
        Assert.Contains("permessage-deflate", response);
        Assert.Contains("server_no_context_takeover", response);
        Assert.Contains("client_no_context_takeover", response);

        deflate.Dispose();
    }

    [Fact]
    public void TryNegotiate_ClientRequestsServerNoContextTakeover()
    {
        WsCompressionOptions options = new()
        {
            Enabled = true,
            ServerNoContextTakeover = false,
        };

        (WsPerMessageDeflate? deflate, string? response) =
            WsPerMessageDeflate.TryNegotiate("permessage-deflate; server_no_context_takeover", options);

        Assert.NotNull(deflate);
        Assert.Contains("server_no_context_takeover", response);
        deflate.Dispose();
    }

    [Fact]
    public void ParseServerResponse_NullReturnsNull()
    {
        WsCompressionOptions options = new() { Enabled = true };
        WsPerMessageDeflate? deflate = WsPerMessageDeflate.ParseServerResponse(null, options);
        Assert.Null(deflate);
    }

    [Fact]
    public void ParseServerResponse_ValidResponse()
    {
        WsCompressionOptions options = new()
        {
            Enabled = true,
            CompressionLevel = CompressionLevel.Fastest,
        };

        WsPerMessageDeflate? deflate = WsPerMessageDeflate.ParseServerResponse(
            "permessage-deflate; server_no_context_takeover; client_no_context_takeover", options);

        Assert.NotNull(deflate);

        // Verify it works
        byte[] original = "Test compression"u8.ToArray();
        byte[] compressed = deflate.Compress(original);
        byte[] decompressed = deflate.Decompress(compressed);
        Assert.Equal(original, decompressed);

        deflate.Dispose();
    }

    [Fact]
    public void BuildOfferHeader_BasicOffer()
    {
        WsCompressionOptions options = new()
        {
            Enabled = true,
            ServerNoContextTakeover = true,
            ClientNoContextTakeover = true,
        };

        string offer = WsPerMessageDeflate.BuildOfferHeader(options);
        Assert.Contains("permessage-deflate", offer);
        Assert.Contains("server_no_context_takeover", offer);
        Assert.Contains("client_no_context_takeover", offer);
    }

    [Fact]
    public void BuildOfferHeader_WithWindowBits()
    {
        WsCompressionOptions options = new()
        {
            Enabled = true,
            ServerMaxWindowBits = 12,
            ClientMaxWindowBits = 10,
        };

        string offer = WsPerMessageDeflate.BuildOfferHeader(options);
        Assert.Contains("server_max_window_bits=12", offer);
        Assert.Contains("client_max_window_bits=10", offer);
    }

    [Fact]
    public void MultipleMessages_NoContextTakeover()
    {
        using WsPerMessageDeflate deflate = new(
            CompressionLevel.Fastest, minMessageSize: 0,
            compressNoContextTakeover: true, decompressNoContextTakeover: true);

        for (int i = 0; i < 10; i++)
        {
            byte[] original = System.Text.Encoding.UTF8.GetBytes($"Message number {i} with some padding data to make it compressible");
            byte[] compressed = deflate.Compress(original);
            byte[] decompressed = deflate.Decompress(compressed);
            Assert.Equal(original, decompressed);
        }
    }
}
