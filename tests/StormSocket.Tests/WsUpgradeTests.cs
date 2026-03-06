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

    #region WsUpgradeContext Tests

    [Fact]
    public void TryParseUpgradeRequest_WithContext_ExtractsPathAndQuery()
    {
        string request = "GET /chat?room=general&token=abc HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(request);
        ReadOnlySequence<byte> buffer = new(bytes);

        WsUpgradeResult result = WsUpgradeHandler.TryParseUpgradeRequest(ref buffer, out WsUpgradeContext? context, null);

        Assert.Equal(WsUpgradeResult.Success, result);
        Assert.NotNull(context);
        Assert.Equal("/chat", context!.Path);
        Assert.Equal("room=general&token=abc", context.QueryString);
        Assert.Equal("general", context.Query["room"]);
        Assert.Equal("abc", context.Query["token"]);
    }

    [Fact]
    public void TryParseUpgradeRequest_WithContext_ExtractsAllHeaders()
    {
        string request = "GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\nAuthorization: Bearer mytoken\r\nX-Custom: test\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(request);
        ReadOnlySequence<byte> buffer = new(bytes);

        WsUpgradeResult result = WsUpgradeHandler.TryParseUpgradeRequest(ref buffer, out WsUpgradeContext? context, null);

        Assert.Equal(WsUpgradeResult.Success, result);
        Assert.NotNull(context);
        Assert.Equal("Bearer mytoken", context!.Headers["Authorization"]);
        Assert.Equal("test", context.Headers["X-Custom"]);
        Assert.Equal("localhost", context.Headers["Host"]);
    }

    [Fact]
    public void WsUpgradeContext_Accept_SetsIsAccepted()
    {
        string request = "GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(request);
        ReadOnlySequence<byte> buffer = new(bytes);

        WsUpgradeHandler.TryParseUpgradeRequest(ref buffer, out WsUpgradeContext? context, null);

        context!.Accept();

        Assert.True(context.IsHandled);
        Assert.True(context.IsAccepted);
    }

    [Fact]
    public void WsUpgradeContext_Reject_SetsStatusCode()
    {
        string request = "GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(request);
        ReadOnlySequence<byte> buffer = new(bytes);

        WsUpgradeHandler.TryParseUpgradeRequest(ref buffer, out WsUpgradeContext? context, null);

        context!.Reject(401, "Invalid token");

        Assert.True(context.IsHandled);
        Assert.False(context.IsAccepted);
        Assert.Equal(401, context.RejectStatusCode);
        Assert.Equal("Invalid token", context.RejectReason);
    }

    [Fact]
    public void WsUpgradeContext_DoubleHandle_Throws()
    {
        string request = "GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(request);
        ReadOnlySequence<byte> buffer = new(bytes);

        WsUpgradeHandler.TryParseUpgradeRequest(ref buffer, out WsUpgradeContext? context, null);

        context!.Accept();

        Assert.Throws<InvalidOperationException>(() => context.Accept());
        Assert.Throws<InvalidOperationException>(() => context.Reject());
    }

    [Fact]
    public void BuildRejectResponse_Returns401()
    {
        byte[] response = WsUpgradeHandler.BuildRejectResponse(401, "Invalid token");
        string responseStr = Encoding.ASCII.GetString(response);

        Assert.StartsWith("HTTP/1.1 401 Unauthorized", responseStr);
        Assert.Contains("Invalid token", responseStr);
    }

    #endregion

    #region Subprotocol Negotiation (RFC 6455 Section 4)

    [Fact]
    public void TryParseUpgradeRequest_WithSubprotocols_ParsesRequestedList()
    {
        string request = "GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Protocol: mqtt, graphql-ws\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(request);
        ReadOnlySequence<byte> buffer = new(bytes);

        WsUpgradeHandler.TryParseUpgradeRequest(ref buffer, out WsUpgradeContext? context, null);

        Assert.NotNull(context);
        Assert.Equal(2, context!.RequestedSubprotocols.Count);
        Assert.Equal("mqtt", context.RequestedSubprotocols[0]);
        Assert.Equal("graphql-ws", context.RequestedSubprotocols[1]);
    }

    [Fact]
    public void TryParseUpgradeRequest_NoSubprotocolHeader_ReturnsEmptyList()
    {
        string request = "GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(request);
        ReadOnlySequence<byte> buffer = new(bytes);

        WsUpgradeHandler.TryParseUpgradeRequest(ref buffer, out WsUpgradeContext? context, null);

        Assert.NotNull(context);
        Assert.Empty(context!.RequestedSubprotocols);
    }

    [Fact]
    public void AcceptSubprotocol_SetsSelectedAndAccepts()
    {
        string request = "GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Protocol: mqtt, graphql-ws\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(request);
        ReadOnlySequence<byte> buffer = new(bytes);

        WsUpgradeHandler.TryParseUpgradeRequest(ref buffer, out WsUpgradeContext? context, null);

        context!.AcceptSubprotocol("mqtt");

        Assert.True(context.IsHandled);
        Assert.True(context.IsAccepted);
        Assert.Equal("mqtt", context.SelectedSubprotocol);
    }

    [Fact]
    public void AcceptSubprotocol_NullOrEmpty_Throws()
    {
        string request = "GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(request);
        ReadOnlySequence<byte> buffer = new(bytes);

        WsUpgradeHandler.TryParseUpgradeRequest(ref buffer, out WsUpgradeContext? context, null);

        Assert.Throws<ArgumentException>(() => context!.AcceptSubprotocol(""));
        Assert.Throws<ArgumentException>(() => context!.AcceptSubprotocol(null!));
    }

    [Fact]
    public void BuildUpgradeResponse_WithSubprotocol_IncludesHeader()
    {
        byte[] response = WsUpgradeHandler.BuildUpgradeResponse("dGhlIHNhbXBsZSBub25jZQ==", subprotocol: "mqtt");
        string responseStr = Encoding.ASCII.GetString(response);

        Assert.Contains("Sec-WebSocket-Protocol: mqtt\r\n", responseStr);
    }

    [Fact]
    public void BuildUpgradeResponse_WithoutSubprotocol_NoHeader()
    {
        byte[] response = WsUpgradeHandler.BuildUpgradeResponse("dGhlIHNhbXBsZSBub25jZQ==");
        string responseStr = Encoding.ASCII.GetString(response);

        Assert.DoesNotContain("Sec-WebSocket-Protocol", responseStr);
    }

    [Fact]
    public void BuildUpgradeRequest_WithSubprotocols_IncludesHeader()
    {
        (byte[] request, _) = WsUpgradeHandler.BuildUpgradeRequest(
            new Uri("ws://localhost:8080"),
            subprotocols: ["mqtt", "graphql-ws"]);
        string requestStr = Encoding.ASCII.GetString(request);

        Assert.Contains("Sec-WebSocket-Protocol: mqtt, graphql-ws\r\n", requestStr);
    }

    [Fact]
    public void BuildUpgradeRequest_NoSubprotocols_NoHeader()
    {
        (byte[] request, _) = WsUpgradeHandler.BuildUpgradeRequest(new Uri("ws://localhost:8080"));
        string requestStr = Encoding.ASCII.GetString(request);

        Assert.DoesNotContain("Sec-WebSocket-Protocol", requestStr);
    }

    [Fact]
    public void TryParseUpgradeResponse_WithSubprotocol_ExtractsIt()
    {
        string responseStr = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=\r\nSec-WebSocket-Protocol: mqtt\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(responseStr);
        ReadOnlySequence<byte> buffer = new(bytes);

        bool ok = WsUpgradeHandler.TryParseUpgradeResponse(ref buffer, "dGhlIHNhbXBsZSBub25jZQ==", out _, out string? subprotocol);

        Assert.True(ok);
        Assert.Equal("mqtt", subprotocol);
    }

    [Fact]
    public void TryParseUpgradeResponse_NoSubprotocol_ReturnsNull()
    {
        string responseStr = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(responseStr);
        ReadOnlySequence<byte> buffer = new(bytes);

        bool ok = WsUpgradeHandler.TryParseUpgradeResponse(ref buffer, "dGhlIHNhbXBsZSBub25jZQ==", out _, out string? subprotocol);

        Assert.True(ok);
        Assert.Null(subprotocol);
    }

    #endregion
}