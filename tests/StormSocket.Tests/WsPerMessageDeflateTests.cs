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
        byte[] decompressed = deflate.Decompress(compressed, 16 * 1024 * 1024);

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
        byte[] decompressed = deflate.Decompress(compressed, 16 * 1024 * 1024);

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
        byte[] decompressed = deflate.Decompress(compressed, 16 * 1024 * 1024);

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
        byte[] decompressed = deflate.Decompress(compressed, 16 * 1024 * 1024);
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
    public void BuildOfferHeader_DoesNotAdvertiseWindowBitsItCannotHonor()
    {
        WsCompressionOptions options = new()
        {
            Enabled = true,
            ServerMaxWindowBits = 12,
            ClientMaxWindowBits = 10,
        };

        // DeflateStream always uses the full 15-bit window, so offering a smaller one would be a
        // promise the client cannot keep — RFC 7692 Section 7.1.2 expects the parameter to be honored.
        string offer = WsPerMessageDeflate.BuildOfferHeader(options);
        Assert.Contains("permessage-deflate", offer);
        Assert.DoesNotContain("max_window_bits", offer);
    }

    [Fact]
    public void TryNegotiate_ClientRequiresSmallerServerWindow_DeclinesTheExtension()
    {
        WsCompressionOptions options = new() { Enabled = true };

        (WsPerMessageDeflate? deflate, string? response) = WsPerMessageDeflate.TryNegotiate(
            "permessage-deflate; server_max_window_bits=10", options);

        Assert.Null(deflate);
        Assert.Null(response);
    }

    [Fact]
    public void TryNegotiate_ClientMaxWindowBitsNotOffered_IsNotSentUnsolicited()
    {
        WsCompressionOptions options = new() { Enabled = true, ClientMaxWindowBits = 10 };

        (WsPerMessageDeflate? deflate, string? response) = WsPerMessageDeflate.TryNegotiate(
            "permessage-deflate", options);

        Assert.NotNull(deflate);
        deflate!.Dispose();
        Assert.DoesNotContain("client_max_window_bits", response);
    }

    [Fact]
    public void TryNegotiate_UnknownParameter_DeclinesTheOffer()
    {
        WsCompressionOptions options = new() { Enabled = true };

        (WsPerMessageDeflate? deflate, string? response) = WsPerMessageDeflate.TryNegotiate(
            "permessage-deflate; nonsense_parameter=1", options);

        Assert.Null(deflate);
        Assert.Null(response);
    }

    [Fact]
    public void TryNegotiate_SecondOfferIsAcceptedWhenTheFirstCannotBeHonored()
    {
        WsCompressionOptions options = new() { Enabled = true };

        (WsPerMessageDeflate? deflate, string? response) = WsPerMessageDeflate.TryNegotiate(
            "permessage-deflate; server_max_window_bits=10, permessage-deflate", options);

        Assert.NotNull(deflate);
        deflate!.Dispose();
        Assert.Contains("permessage-deflate", response);
    }

    [Fact]
    public void Decompress_BeyondTheLimit_ThrowsMessageTooBig()
    {
        using WsPerMessageDeflate deflate = new(
            CompressionLevel.Optimal, minMessageSize: 0,
            compressNoContextTakeover: true, decompressNoContextTakeover: true);

        byte[] compressed = deflate.Compress(new byte[4 * 1024 * 1024]);

        WsProtocolException ex = Assert.Throws<WsProtocolException>(() => deflate.Decompress(compressed, 64 * 1024));
        Assert.Equal(WsCloseStatus.MessageTooBig, ex.CloseStatus);
    }

    [Fact]
    public void Decompress_ExactlyAtTheLimit_Succeeds()
    {
        using WsPerMessageDeflate deflate = new(
            CompressionLevel.Optimal, minMessageSize: 0,
            compressNoContextTakeover: true, decompressNoContextTakeover: true);

        byte[] payload = new byte[64 * 1024];
        Random.Shared.NextBytes(payload);
        byte[] compressed = deflate.Compress(payload);

        Assert.Equal(payload, deflate.Decompress(compressed, payload.Length));
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
            byte[] decompressed = deflate.Decompress(compressed, 16 * 1024 * 1024);
            Assert.Equal(original, decompressed);
        }
    }
}
