using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StormSocket.Server;

namespace StormSocket.Extensions.Hosting;

/// <summary>
/// Reports whether a registered StormSocket server is listening, for use with
/// <c>MapHealthChecks</c> and orchestrator readiness probes.
/// </summary>
public static class HealthCheckExtensions
{
    /// <summary>Adds a health check that reports the WebSocket server's state and connection count.</summary>
    /// <param name="name">Health check name. Defaults to <c>stormsocket-websocket</c>.</param>
    /// <param name="failureStatus">Status to report when the server is not listening. Defaults to Unhealthy.</param>
    public static IHealthChecksBuilder AddStormWebSocketServer(
        this IHealthChecksBuilder builder,
        string name = "stormsocket-websocket",
        HealthStatus failureStatus = HealthStatus.Unhealthy)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Add(new HealthCheckRegistration(
            name,
            provider => new StormServerHealthCheck(
                () => provider.GetRequiredService<StormWebSocketServer>().IsRunning,
                () => provider.GetRequiredService<StormWebSocketServer>().Sessions.Count,
                failureStatus),
            failureStatus,
            tags: null));
    }

    /// <summary>Adds a health check that reports the TCP server's state and connection count.</summary>
    /// <inheritdoc cref="AddStormWebSocketServer" path="/param"/>
    public static IHealthChecksBuilder AddStormTcpServer(
        this IHealthChecksBuilder builder,
        string name = "stormsocket-tcp",
        HealthStatus failureStatus = HealthStatus.Unhealthy)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Add(new HealthCheckRegistration(
            name,
            provider => new StormServerHealthCheck(
                () => provider.GetRequiredService<StormTcpServer>().IsRunning,
                () => provider.GetRequiredService<StormTcpServer>().Sessions.Count,
                failureStatus),
            failureStatus,
            tags: null));
    }
}

/// <summary>
/// Health check over a server's listening state.
/// </summary>
/// <remarks>
/// Takes the state as delegates rather than a server instance so that both server types share one
/// implementation without a common base type having to exist in the core library.
/// </remarks>
internal sealed class StormServerHealthCheck(
    Func<bool> isRunning,
    Func<int> activeConnections,
    HealthStatus failureStatus) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!isRunning())
        {
            return Task.FromResult(new HealthCheckResult(failureStatus, "The server is not listening."));
        }

        Dictionary<string, object> data = new(1) { ["activeConnections"] = activeConnections() };
        return Task.FromResult(HealthCheckResult.Healthy("The server is listening.", data));
    }
}
