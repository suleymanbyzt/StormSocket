namespace StormSocket.Session;

/// <summary>
/// Extended session interface for WebSocket connections.
/// Adds text frame sending capabilities on top of <see cref="ISession"/>.
/// <para>
/// WebSocket server events (<c>OnConnected</c>, <c>OnMessageReceived</c>, etc.)
/// receive this interface directly — no casting needed.
/// </para>
/// <example>
/// <code>
/// server.OnMessageReceived += async (session, msg) =>
/// {
///     await session.SendTextAsync($"Echo: {msg.Text}");
/// };
/// </code>
/// </example>
/// </summary>
public interface IWebSocketSession : ISession
{
    /// <summary>Sends a Text WebSocket frame (UTF-8 encoded).</summary>
    ValueTask SendTextAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Sends a Text WebSocket frame from pre-encoded UTF-8 bytes (zero-copy).</summary>
    ValueTask SendTextAsync(ReadOnlyMemory<byte> utf8Data, CancellationToken cancellationToken = default);
}
