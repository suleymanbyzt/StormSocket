using StormSocket.Core;
using StormSocket.Events;
using StormSocket.Session;

namespace StormSocket.Extensions.Hosting;

/// <summary>
/// Handles the lifetime and messages of WebSocket connections, resolved from dependency injection.
/// </summary>
/// <remarks>
/// Implement only the members you need; the rest default to doing nothing. Register with
/// <c>AddStormWebSocketServer(...).AddHandler&lt;MyHandler&gt;()</c>. Several handlers may be
/// registered, and each one is invoked in registration order.
/// <para>
/// Handlers are resolved per invocation from a scope by default, so constructor-injecting scoped
/// services such as a <c>DbContext</c> is safe. Register the handler as a singleton when the extra
/// scope per message costs more than it is worth.
/// </para>
/// <example>
/// <code>
/// public sealed class ChatHandler : IWebSocketHandler
/// {
///     private readonly IMessageStore _store;
///
///     public ChatHandler(IMessageStore store) =&gt; _store = store;
///
///     public async ValueTask OnMessageAsync(IWebSocketSession session, WsMessage message, CancellationToken cancellationToken)
///     {
///         await _store.SaveAsync(message.Text, cancellationToken);
///         await session.SendTextAsync(message.Text, cancellationToken);
///     }
/// }
/// </code>
/// </example>
/// </remarks>
public interface IWebSocketHandler
{
    /// <summary>Called after the WebSocket handshake completes.</summary>
    ValueTask OnConnectedAsync(IWebSocketSession session, CancellationToken cancellationToken) => default;

    /// <summary>
    /// Called for every complete text or binary message.
    /// </summary>
    /// <remarks>
    /// <see cref="WsMessage.Data"/> points into a buffer the connection reuses for the next frame, so
    /// it is valid until this method returns. Copy it if it outlives the call.
    /// </remarks>
    ValueTask OnMessageAsync(IWebSocketSession session, WsMessage message, CancellationToken cancellationToken);

    /// <summary>Called after the connection is gone, whatever the cause.</summary>
    ValueTask OnDisconnectedAsync(IWebSocketSession session, DisconnectReason reason, CancellationToken cancellationToken) => default;
}
