using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StormSocket.Middleware;
using StormSocket.Server;

namespace StormSocket.Extensions.Hosting;

/// <summary>
/// Starts and stops the WebSocket server with the application.
/// </summary>
/// <remarks>
/// Start failures are deliberately allowed to propagate: a host that came up "successfully" with a
/// server that never bound its port is far worse than one that refuses to start and says why.
/// </remarks>
internal sealed class StormWebSocketServerHostedService(
    StormWebSocketServer server,
    WebSocketHandlerDispatcher dispatcher,
    IEnumerable<IConnectionMiddleware> middlewares,
    IHostApplicationLifetime lifetime,
    ILogger<StormWebSocketServerHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (IConnectionMiddleware middleware in middlewares)
        {
            server.UseMiddleware(middleware);
        }

        // Handlers receive the application's stopping token, so long-running work inside a handler
        // can react to shutdown instead of being abandoned mid-flight.
        dispatcher.Attach(server, lifetime.ApplicationStopping);

        await server.StartAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("StormSocket WebSocket server listening on {EndPoint}", server.LocalEndPoint);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await server.StopAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("StormSocket WebSocket server stopped");
    }
}

/// <summary>Starts and stops the TCP server with the application.</summary>
internal sealed class StormTcpServerHostedService(
    StormTcpServer server,
    TcpHandlerDispatcher dispatcher,
    IEnumerable<IConnectionMiddleware> middlewares,
    IHostApplicationLifetime lifetime,
    ILogger<StormTcpServerHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (IConnectionMiddleware middleware in middlewares)
        {
            server.UseMiddleware(middleware);
        }

        dispatcher.Attach(server, lifetime.ApplicationStopping);

        await server.StartAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("StormSocket TCP server listening on {EndPoint}", server.LocalEndPoint);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await server.StopAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("StormSocket TCP server stopped");
    }
}
