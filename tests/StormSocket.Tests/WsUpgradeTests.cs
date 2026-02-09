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

        string[] lines = responseStr.Split("\r\n");
        string? acceptLine = lines.FirstOrDefault(l => l.StartsWith("Sec-WebSocket-Accept:"));
        Assert.NotNull(acceptLine);

        string actualAccept = acceptLine!.Substring("Sec-WebSocket-Accept:".Length).Trim();

        if (expectedAccept is not null)
            Assert.Equal(expectedAccept, actualAccept);

        Assert.Equal(28, actualAccept.Length);
    }

    [Fact]
    public void TryParseUpgradeRequest_ValidRequest_Success()
    {
        string request = "GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(request);
        ReadOnlySequence<byte> buffer = new(bytes);

        Assert.Equal(WsUpgradeResult.Success, WsUpgradeHandler.TryParseUpgradeRequest(ref buffer, out string? wsKey));
        Assert.Equal("dGhlIHNhbXBsZSBub25jZQ==", wsKey);
    }

    [Fact]
    public void TryParseUpgradeRequest_ConnectionWithMultipleTokens_Success()
    {
        string request = "GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: keep-alive, Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(request);
        ReadOnlySequence<byte> buffer = new(bytes);

        Assert.Equal(WsUpgradeResult.Success, WsUpgradeHandler.TryParseUpgradeRequest(ref buffer, out _));
    }

    [Fact]
    public void TryParseUpgradeRequest_Incomplete_ReturnsIncomplete()
    {
        string request = "GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket";
        byte[] bytes = Encoding.ASCII.GetBytes(request);
        ReadOnlySequence<byte> buffer = new(bytes);

        Assert.Equal(WsUpgradeResult.Incomplete, WsUpgradeHandler.TryParseUpgradeRequest(ref buffer, out _));
    }

    [Theory]
    [InlineData("", WsUpgradeResult.MissingUpgradeHeader)]           // missing
    [InlineData("Upgrade: http", WsUpgradeResult.MissingUpgradeHeader)] // invalid value
    public void TryParseUpgradeRequest_InvalidUpgrade(string upgradeHeader, WsUpgradeResult expected)
    {
        string headers = string.IsNullOrEmpty(upgradeHeader) ? "" : $"{upgradeHeader}\r\n";
        string request = $"GET / HTTP/1.1\r\nHost: localhost\r\n{headers}Connection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(request);
        ReadOnlySequence<byte> buffer = new(bytes);

        Assert.Equal(expected, WsUpgradeHandler.TryParseUpgradeRequest(ref buffer, out _));
    }

    [Theory]
    [InlineData("", WsUpgradeResult.MissingConnectionHeader)]              // missing
    [InlineData("Connection: keep-alive", WsUpgradeResult.MissingConnectionHeader)] // invalid value
    public void TryParseUpgradeRequest_InvalidConnection(string connectionHeader, WsUpgradeResult expected)
    {
        string headers = string.IsNullOrEmpty(connectionHeader) ? "" : $"{connectionHeader}\r\n";
        string request = $"GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\n{headers}Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(request);
        ReadOnlySequence<byte> buffer = new(bytes);

        Assert.Equal(expected, WsUpgradeHandler.TryParseUpgradeRequest(ref buffer, out _));
    }

    [Theory]
    [InlineData("", WsUpgradeResult.InvalidVersion)]                        // missing
    [InlineData("Sec-WebSocket-Version: 8", WsUpgradeResult.InvalidVersion)] // wrong version
    public void TryParseUpgradeRequest_InvalidVersion(string versionHeader, WsUpgradeResult expected)
    {
        string headers = string.IsNullOrEmpty(versionHeader) ? "" : $"{versionHeader}\r\n";
        string request = $"GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n{headers}\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(request);
        ReadOnlySequence<byte> buffer = new(bytes);

        Assert.Equal(expected, WsUpgradeHandler.TryParseUpgradeRequest(ref buffer, out _));
    }

    [Fact]
    public void TryParseUpgradeRequest_MissingKey_ReturnsMissingKey()
    {
        string request = "GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(request);
        ReadOnlySequence<byte> buffer = new(bytes);

        Assert.Equal(WsUpgradeResult.MissingKey, WsUpgradeHandler.TryParseUpgradeRequest(ref buffer, out _));
    }

    [Fact]
    public void BuildErrorResponse_InvalidVersion_IncludesVersionHeader()
    {
        // RFC 6455 4.4: Server MUST respond with Sec-WebSocket-Version header
        byte[] response = WsUpgradeHandler.BuildErrorResponse(WsUpgradeResult.InvalidVersion);
        string responseStr = Encoding.ASCII.GetString(response);

        Assert.StartsWith("HTTP/1.1 400 Bad Request", responseStr);
        Assert.Contains("Sec-WebSocket-Version: 13", responseStr);
    }

    #region Origin Validation (RFC 6455 10.2)

    [Fact]
    public void TryParseUpgradeRequest_NoAllowedOrigins_AllowsAny()
    {
        string request = "GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\nOrigin: https://evil.com\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(request);
        ReadOnlySequence<byte> buffer = new(bytes);

        // No allowedOrigins = allow all
        Assert.Equal(WsUpgradeResult.Success, WsUpgradeHandler.TryParseUpgradeRequest(ref buffer, out _));
    }

    [Fact]
    public void TryParseUpgradeRequest_OriginInAllowedList_Success()
    {
        string request = "GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\nOrigin: https://myapp.com\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(request);
        ReadOnlySequence<byte> buffer = new(bytes);

        string[] allowedOrigins = ["https://myapp.com", "https://staging.myapp.com"];
        Assert.Equal(WsUpgradeResult.Success, WsUpgradeHandler.TryParseUpgradeRequest(ref buffer, out _, allowedOrigins));
    }

    [Fact]
    public void TryParseUpgradeRequest_OriginNotInAllowedList_ReturnsForbidden()
    {
        string request = "GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\nOrigin: https://evil.com\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(request);
        ReadOnlySequence<byte> buffer = new(bytes);

        string[] allowedOrigins = ["https://myapp.com"];
        Assert.Equal(WsUpgradeResult.ForbiddenOrigin, WsUpgradeHandler.TryParseUpgradeRequest(ref buffer, out _, allowedOrigins));
    }

    [Fact]
    public void TryParseUpgradeRequest_NoOriginHeaderWithAllowedList_ReturnsForbidden()
    {
        // Non-browser clients may not send Origin header - should be rejected if allowedOrigins is set
        string request = "GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(request);
        ReadOnlySequence<byte> buffer = new(bytes);

        string[] allowedOrigins = ["https://myapp.com"];
        Assert.Equal(WsUpgradeResult.ForbiddenOrigin, WsUpgradeHandler.TryParseUpgradeRequest(ref buffer, out _, allowedOrigins));
    }

    [Fact]
    public void TryParseUpgradeRequest_OriginCaseInsensitive_Success()
    {
        string request = "GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\nOrigin: HTTPS://MYAPP.COM\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(request);
        ReadOnlySequence<byte> buffer = new(bytes);

        string[] allowedOrigins = ["https://myapp.com"];
        Assert.Equal(WsUpgradeResult.Success, WsUpgradeHandler.TryParseUpgradeRequest(ref buffer, out _, allowedOrigins));
    }

    [Fact]
    public void BuildErrorResponse_ForbiddenOrigin_Returns403()
    {
        byte[] response = WsUpgradeHandler.BuildErrorResponse(WsUpgradeResult.ForbiddenOrigin);
        string responseStr = Encoding.ASCII.GetString(response);

        Assert.StartsWith("HTTP/1.1 403 Forbidden", responseStr);
        Assert.Contains("Origin not allowed", responseStr);
    }

    #endregion
}