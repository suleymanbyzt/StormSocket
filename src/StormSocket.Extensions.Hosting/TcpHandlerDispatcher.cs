using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StormSocket.Core;
using StormSocket.Server;
using StormSocket.Session;

namespace StormSocket.Extensions.Hosting;

/// <summary>
/// Bridges the TCP server's events to <see cref="ITcpConnectionHandler"/> implementations resolved
/// from dependency injection. Mirrors <see cref="WebSocketHandlerDispatcher"/>.
/// </summary>
internal sealed class TcpHandlerDispatcher(
    IServiceProvider services,
    TcpServerRegistration registration,
    ILogger<TcpHandlerDispatcher> logger)
{
    private ITcpConnectionHandler[]? _singletonHandlers;
    private CancellationToken _shutdownToken;

    /// <summary>Subscribes to the server's events. Called once, before the server starts.</summary>
    public void Attach(StormTcpServer server, CancellationToken shutdownToken)
    {
        _shutdownToken = shutdownToken;

        if (!registration.RequiresScope)
        {
            _singletonHandlers = services.GetServices<ITcpConnectionHandler>().ToArray();

            if (_singletonHandlers.Length == 0)
            {
                return;
            }
        }

        server.OnConnected += OnConnectedAsync;
        server.OnDataReceived += OnDataReceivedAsync;
        server.OnDisconnected += OnDisconnectedAsync;
    }

    private async ValueTask OnConnectedAsync(ISession session)
    {
        if (_singletonHandlers is { } handlers)
        {
            foreach (ITcpConnectionHandler handler in handlers)
            {
                await InvokeConnectedAsync(handler, session).ConfigureAwait(false);
            }

            return;
        }

        using IServiceScope scope = services.CreateScope();
        foreach (ITcpConnectionHandler handler in scope.ServiceProvider.GetServices<ITcpConnectionHandler>())
        {
            await InvokeConnectedAsync(handler, session).ConfigureAwait(false);
        }
    }

    private async ValueTask OnDataReceivedAsync(ISession session, ReadOnlyMemory<byte> data)
    {
        if (_singletonHandlers is { } handlers)
        {
            foreach (ITcpConnectionHandler handler in handlers)
            {
                await InvokeDataAsync(handler, session, data).ConfigureAwait(false);
            }

            return;
        }

        using IServiceScope scope = services.CreateScope();
        foreach (ITcpConnectionHandler handler in scope.ServiceProvider.GetServices<ITcpConnectionHandler>())
        {
            await InvokeDataAsync(handler, session, data).ConfigureAwait(false);
        }
    }

    private async ValueTask OnDisconnectedAsync(ISession session, DisconnectReason reason)
    {
        if (_singletonHandlers is { } handlers)
        {
            foreach (ITcpConnectionHandler handler in handlers)
            {
                await InvokeDisconnectedAsync(handler, session, reason).ConfigureAwait(false);
            }

            return;
        }

        using IServiceScope scope = services.CreateScope();
        foreach (ITcpConnectionHandler handler in scope.ServiceProvider.GetServices<ITcpConnectionHandler>())
        {
            await InvokeDisconnectedAsync(handler, session, reason).ConfigureAwait(false);
        }
    }

    private async ValueTask InvokeConnectedAsync(ITcpConnectionHandler handler, ISession session)
    {
        try
        {
            await handler.OnConnectedAsync(session, _shutdownToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Handler}.OnConnectedAsync threw for session {SessionId}", handler.GetType().Name, session.Id);
        }
    }

    private async ValueTask InvokeDataAsync(ITcpConnectionHandler handler, ISession session, ReadOnlyMemory<byte> data)
    {
        try
        {
            await handler.OnDataReceivedAsync(session, data, _shutdownToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Handler}.OnDataReceivedAsync threw for session {SessionId}", handler.GetType().Name, session.Id);
        }
    }

    private async ValueTask InvokeDisconnectedAsync(ITcpConnectionHandler handler, ISession session, DisconnectReason reason)
    {
        try
        {
            await handler.OnDisconnectedAsync(session, reason, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Handler}.OnDisconnectedAsync threw for session {SessionId}", handler.GetType().Name, session.Id);
        }
    }
}
