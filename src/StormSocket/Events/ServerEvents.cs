using StormSocket.Session;

namespace StormSocket.Events;

/// <summary>Fired when a new TCP session connects.</summary>
public delegate ValueTask SessionConnectedHandler(ISession session);

/// <summary>Fired when a TCP session disconnects.</summary>
public delegate ValueTask SessionDisconnectedHandler(ISession session);

/// <summary>Fired when raw data (or a framed message) is received from a TCP session.</summary>
public delegate ValueTask DataReceivedHandler(ISession session, ReadOnlyMemory<byte> data);

/// <summary>Fired when an error occurs. Session may be null if the error happened before session creation.</summary>
public delegate ValueTask ErrorHandler(ISession? session, Exception exception);

/// <summary>Fired when a complete WebSocket message (text or binary) is received.</summary>
public delegate ValueTask WsMessageReceivedHandler(ISession session, WsMessage message);

/// <summary>Fired when a WebSocket client completes the upgrade handshake.</summary>
public delegate ValueTask WsConnectedHandler(ISession session);

/// <summary>Fired when a WebSocket client disconnects.</summary>
public delegate ValueTask WsDisconnectedHandler(ISession session);