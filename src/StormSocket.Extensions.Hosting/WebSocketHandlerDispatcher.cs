using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StormSocket.Core;
using StormSocket.Events;
using StormSocket.Server;
using StormSocket.Session;

namespace StormSocket.Extensions.Hosting;

/// <summary>
/// Bridges the server's events to <see cref="IWebSocketHandler"/> implementations resolved from
/// dependency injection.
/// </summary>
/// <remarks>
/// When every handler is registered as a singleton they are resolved once and reused, which keeps
/// the message path free of per-message allocation. As soon as one handler is scoped or transient,
/// each invocation gets its own scope so that scoped dependencies behave the way they do in a web
/// request. The three call sites are written out rather than sharing a delegate-taking helper,
/// because that delegate would allocate a closure for every message.
/// </remarks>
internal sealed class WebSocketHandlerDispatcher(
    IServiceProvider services,
    WebSocketServerRegistration registration,
    ILogger<WebSocketHandlerDispatcher> logger)
{
    private IWebSocketHandler[]? _singletonHandlers;
    private CancellationToken _shutdownToken;

    /// <summary>Subscribes to the server's events. Called once, before the server starts.</summary>
    public void Attach(StormWebSocketServer server, CancellationToken shutdownToken)
    {
        _shutdownToken = shutdownToken;

        if (!registration.RequiresScope)
        {
            _singletonHandlers = services.GetServices<IWebSocketHandler>().ToArray();

            if (_singletonHandlers.Length == 0)
            {
                return;
            }
        }

        server.OnConnected += OnConnectedAsync;
        server.OnMessageReceived += OnMessageReceivedAsync;
        server.OnDisconnected += OnDisconnectedAsync;
    }

    private async ValueTask OnConnectedAsync(IWebSocketSession session)
    {
        if (_singletonHandlers is { } handlers)
        {
            foreach (IWebSocketHandler handler in handlers)
            {
                await InvokeConnectedAsync(handler, session).ConfigureAwait(false);
            }

            return;
        }

        using IServiceScope scope = services.CreateScope();
        foreach (IWebSocketHandler handler in scope.ServiceProvider.GetServices<IWebSocketHandler>())
        {
            await InvokeConnectedAsync(handler, session).ConfigureAwait(false);
        }
    }

    private async ValueTask OnMessageReceivedAsync(IWebSocketSession session, WsMessage message)
    {
        if (_singletonHandlers is { } handlers)
        {
            foreach (IWebSocketHandler handler in handlers)
            {
                await InvokeMessageAsync(handler, session, message).ConfigureAwait(false);
            }

            return;
        }

        using IServiceScope scope = services.CreateScope();
        foreach (IWebSocketHandler handler in scope.ServiceProvider.GetServices<IWebSocketHandler>())
        {
            await InvokeMessageAsync(handler, session, message).ConfigureAwait(false);
        }
    }

    private async ValueTask OnDisconnectedAsync(IWebSocketSession session, DisconnectReason reason)
    {
        if (_singletonHandlers is { } handlers)
        {
            foreach (IWebSocketHandler handler in handlers)
            {
                await InvokeDisconnectedAsync(handler, session, reason).ConfigureAwait(false);
            }

            return;
        }

        using IServiceScope scope = services.CreateScope();
        foreach (IWebSocketHandler handler in scope.ServiceProvider.GetServices<IWebSocketHandler>())
        {
            await InvokeDisconnectedAsync(handler, session, reason).ConfigureAwait(false);
        }
    }

    // A failing handler is logged and the next one still runs: one misbehaving handler should not
    // silently deprive the others of the event, and it should not take the connection down either.
    private async ValueTask InvokeConnectedAsync(IWebSocketHandler handler, IWebSocketSession session)
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

    private async ValueTask InvokeMessageAsync(IWebSocketHandler handler, IWebSocketSession session, WsMessage message)
    {
        try
        {
            await handler.OnMessageAsync(session, message, _shutdownToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Handler}.OnMessageAsync threw for session {SessionId}", handler.GetType().Name, session.Id);
        }
    }

    private async ValueTask InvokeDisconnectedAsync(IWebSocketHandler handler, IWebSocketSession session, DisconnectReason reason)
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
