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

    /// <summary>Called after a session disconnects. Middlewares are called in reverse order.</summary>
    ValueTask OnDisconnectedAsync(ISession session) => ValueTask.CompletedTask;

    /// <summary>Called when an exception occurs during connection handling.</summary>
    ValueTask OnErrorAsync(ISession session, Exception exception) => ValueTask.CompletedTask;
}