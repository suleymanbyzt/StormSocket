using StormSocket.Core;
using StormSocket.Session;

namespace StormSocket.Middleware;

/// <summary>
/// Intercepts connection lifecycle events and data flow.
/// Implement only the methods you need; all have no-op defaults.
/// Register via <c>server.UseMiddleware(new MyMiddleware())</c>.
/// </summary>
public interface IConnectionMiddleware
{
    /// <summary>Called after a session is fully established (after handshake/upgrade).</summary>
    ValueTask OnConnectedAsync(ISession session) => ValueTask.CompletedTask;

    /// <summary>
    /// Called when data arrives from the client, before the OnDataReceived event fires.
    /// Return modified data to pass downstream, or <see cref="ReadOnlyMemory{T}.Empty"/> to suppress.
    /// </summary>
    ValueTask<ReadOnlyMemory<byte>> OnDataReceivedAsync(ISession session, ReadOnlyMemory<byte> data) => ValueTask.FromResult(data);

    /// <summary>
    /// Called before data is sent to the client.
    /// Return modified data to send, or <see cref="ReadOnlyMemory{T}.Empty"/> to suppress.
    /// </summary>
    ValueTask<ReadOnlyMemory<byte>> OnDataSendingAsync(ISession session, ReadOnlyMemory<byte> data) => ValueTask.FromResult(data);

    /// <summary>
    /// Called for every decoded WebSocket frame, including control frames and individual fragments,
    /// before any of them is acted on. Return false to drop the frame and stop reading.
    /// </summary>
    /// <remarks>
    /// <see cref="OnDataReceivedAsync"/> only ever sees fully assembled application messages, so a
    /// middleware that meters traffic there cannot see a ping flood or a stream of empty fragments —
    /// both of which cost the server work (a ping is auto-ponged) while never producing a message.
    /// </remarks>
    ValueTask<bool> OnFrameReceivedAsync(ISession session) => ValueTask.FromResult(true);

    /// <summary>Called after a session disconnects. Middlewares are called in reverse order.</summary>
    ValueTask OnDisconnectedAsync(ISession session, DisconnectReason reason) => ValueTask.CompletedTask;

    /// <summary>Called when an exception occurs during connection handling.</summary>
    ValueTask OnErrorAsync(ISession session, Exception exception) => ValueTask.CompletedTask;
}