namespace StormSocket.Events;

/// <summary>Fired when the client successfully connects to the server.</summary>
public delegate ValueTask ClientConnectedHandler();

/// <summary>Fired when the client disconnects from the server.</summary>
public delegate ValueTask ClientDisconnectedHandler();

/// <summary>Fired when raw data (or a framed message) is received from the server.</summary>
public delegate ValueTask ClientDataReceivedHandler(ReadOnlyMemory<byte> data);

/// <summary>Fired when a complete WebSocket message (text or binary) is received from the server.</summary>
public delegate ValueTask ClientWsMessageReceivedHandler(WsMessage message);

/// <summary>Fired when an error occurs on the client connection.</summary>
public delegate ValueTask ClientErrorHandler(Exception exception);

/// <summary>Fired when the client is attempting to reconnect after a disconnection.</summary>
public delegate ValueTask ClientReconnectingHandler(int attempt, TimeSpan delay);




