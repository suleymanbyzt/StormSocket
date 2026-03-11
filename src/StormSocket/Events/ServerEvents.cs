using StormSocket.Core;
using StormSocket.Session;
using StormSocket.WebSocket;

namespace StormSocket.Events;

/// <summary>Fired when a new TCP session connects.</summary>
public delegate ValueTask SessionConnectedHandler(ISession session);

/// <summary>Fired when a TCP session disconnects.</summary>
public delegate ValueTask SessionDisconnectedHandler(ISession session, DisconnectReason reason);

/// <summary>Fired when raw data (or a framed message) is received from a TCP session.</summary>
public delegate ValueTask DataReceivedHandler(ISession session, ReadOnlyMemory<byte> data);

/// <summary>Fired when an error occurs. Session may be null if the error happened before session creation.</summary>
public delegate ValueTask ErrorHandler(ISession? session, Exception exception);

/// <summary>Fired when a complete WebSocket message (text or binary) is received.</summary>
public delegate ValueTask WsMessageReceivedHandler(IWebSocketSession session, WsMessage message);

/// <summary>Fired when a WebSocket client completes the upgrade handshake.</summary>
public delegate ValueTask WsConnectedHandler(IWebSocketSession session);

/// <summary>Fired when a WebSocket client disconnects.</summary>
public delegate ValueTask WsDisconnectedHandler(IWebSocketSession session, DisconnectReason reason);

/// <summary>
/// Fired before accepting a WebSocket upgrade request.
/// Use context.Accept() or context.Reject() to control the connection.
/// </summary>
public delegate ValueTask WsConnectingHandler(WsUpgradeContext context);
