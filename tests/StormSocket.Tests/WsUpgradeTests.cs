using System.Buffers;
using System.Text;
using StormSocket.WebSocket;
using Xunit;

namespace StormSocket.Tests;

public class WsUpgradeTests
{
    [Theory]
    [InlineData("dGhlIHNhbXBsZSBub25jZQ==", "s3pPLMBiTxaQ9kYGzzhZRbK+xOo=")] // RFC 6455 test vector
    [InlineData("PPQDmRpxl4iWIq3o/AH6zw==", null)] // actual key - just check it parses
    public void AcceptKey_MatchesRfc6455(string wsKey, string? expectedAccept)
    {
        byte[] response = WsUpgradeHandler.BuildUpgradeResponse(wsKey);
        string responseStr = Encoding.ASCII.GetString(response);

        // Extract Sec-WebSocket-Accept from response
        string[] lines = responseStr.Split("\r\n");
        string? acceptLine = lines.FirstOrDefault(l => l.StartsWith("Sec-WebSocket-Accept:"));
        Assert.NotNull(acceptLine);

        string actualAccept = acceptLine!.Substring("Sec-WebSocket-Accept:".Length).Trim();

        if (expectedAccept is not null)
            Assert.Equal(expectedAccept, actualAccept);

        // Verify it's a valid base64 SHA1 hash (28 chars)
        Assert.Equal(28, actualAccept.Length);
    }

    [Fact]
    public void TryParseUpgradeRequest_ExtractsKey()
    {
        string request = "GET / HTTP/1.1\r\nHost: 127.0.0.1:8080\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: PPQDmRpxl4iWIq3o/AH6zw==\r\nSec-WebSocket-Version: 13\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(request);
        ReadOnlySequence<byte> buffer = new ReadOnlySequence<byte>(bytes);

        Assert.True(WsUpgradeHandler.TryParseUpgradeRequest(ref buffer, out string? wsKey));
        Assert.Equal("PPQDmRpxl4iWIq3o/AH6zw==", wsKey);
    }

    [Fact]
    public void FullRoundTrip_AcceptKeyValid()
    {
        string clientKey = "dGhlIHNhbXBsZSBub25jZQ==";
        string request = $"GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {clientKey}\r\nSec-WebSocket-Version: 13\r\n\r\n";

        byte[] bytes = Encoding.ASCII.GetBytes(request);
        ReadOnlySequence<byte> buffer = new ReadOnlySequence<byte>(bytes);

        Assert.True(WsUpgradeHandler.TryParseUpgradeRequest(ref buffer, out string? parsedKey));
        Assert.Equal(clientKey, parsedKey);

        byte[] response = WsUpgradeHandler.BuildUpgradeResponse(parsedKey!);
        string responseStr = Encoding.ASCII.GetString(response);

        Assert.Contains("Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", responseStr);
    }
}