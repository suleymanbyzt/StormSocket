using StormSocket.Core;
using StormSocket.Session;

namespace StormSocket.Extensions.Hosting;

/// <summary>
/// Handles the lifetime and data of TCP connections, resolved from dependency injection.
/// </summary>
/// <remarks>
/// The TCP counterpart of <see cref="IWebSocketHandler"/>; the same resolution and lifetime rules
/// apply. What arrives in <see cref="OnDataReceivedAsync"/> depends on the configured
/// <see cref="StormSocket.Framing.IMessageFramer"/>: one call per framed message, or whatever the
/// socket happened to deliver when framing is off.
/// </remarks>
public interface ITcpConnectionHandler
{
    /// <summary>Called once the connection is established and ready to use.</summary>
    ValueTask OnConnectedAsync(ISession session, CancellationToken cancellationToken) => default;

    /// <summary>
    /// Called for every framed message, or for every chunk the socket delivered when no framer is configured.
    /// </summary>
    /// <remarks>The data is valid until this method returns; copy it if it outlives the call.</remarks>
    ValueTask OnDataReceivedAsync(ISession session, ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>Called after the connection is gone, whatever the cause.</summary>
    ValueTask OnDisconnectedAsync(ISession session, DisconnectReason reason, CancellationToken cancellationToken) => default;
}
