using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StormSocket.Server;

namespace StormSocket.Extensions.Hosting;

/// <summary>
/// Registers StormSocket servers with the Generic Host: created from the container, started and
/// stopped with the application, and shut down with the host's own drain window.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds a <see cref="StormWebSocketServer"/> that starts and stops with the host.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">
    /// Applied to the server's options when the container builds the server. Several calls compose in
    /// registration order, so a library can set a default that the application later overrides.
    /// </param>
    /// <example>
    /// <code>
    /// builder.Services
    ///     .AddStormWebSocketServer(options =&gt;
    ///     {
    ///         options.EndPoint = new IPEndPoint(IPAddress.Any, 8080);
    ///         options.MaxConnections = 10_000;
    ///         options.WebSocket!.IdleTimeout = TimeSpan.FromMinutes(5);
    ///     })
    ///     .AddHandler&lt;ChatHandler&gt;();
    /// </code>
    /// </example>
    public static IStormWebSocketServerBuilder AddStormWebSocketServer(
        this IServiceCollection services,
        Action<ServerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        WebSocketServerRegistration registration = GetOrAddRegistration<WebSocketServerRegistration>(services, AddWebSocketServices);

        if (configure is not null)
        {
            registration.AddConfiguration(configure);
        }

        return new StormWebSocketServerBuilder(services);
    }

    /// <summary>
    /// Adds a <see cref="StormTcpServer"/> that starts and stops with the host.
    /// </summary>
    /// <inheritdoc cref="AddStormWebSocketServer" path="/param"/>
    public static IStormTcpServerBuilder AddStormTcpServer(
        this IServiceCollection services,
        Action<ServerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        TcpServerRegistration registration = GetOrAddRegistration<TcpServerRegistration>(services, AddTcpServices);

        if (configure is not null)
        {
            registration.AddConfiguration(configure);
        }

        return new StormTcpServerBuilder(services);
    }

    /// <summary>
    /// Returns the existing registration so repeated calls add configuration instead of a second
    /// server. One server of each kind per container keeps <c>GetRequiredService&lt;StormWebSocketServer&gt;()</c>
    /// unambiguous; run two hosts, or two containers, to listen on two ports.
    /// </summary>
    private static TRegistration GetOrAddRegistration<TRegistration>(IServiceCollection services, Action<IServiceCollection> addServices)
        where TRegistration : ServerRegistration, new()
    {
        foreach (ServiceDescriptor descriptor in services)
        {
            if (descriptor.ServiceType == typeof(TRegistration) && descriptor.ImplementationInstance is TRegistration existing)
            {
                return existing;
            }
        }

        TRegistration registration = new();
        services.AddSingleton(registration);
        addServices(services);

        return registration;
    }

    private static void AddWebSocketServices(IServiceCollection services)
    {
        services.AddSingleton(provider =>
        {
            WebSocketServerRegistration registration = provider.GetRequiredService<WebSocketServerRegistration>();
            ServerOptions options = registration.BuildOptions(provider);

            // The host already owns a logger factory; wiring it here is what makes the server's
            // structured logging show up in the application's log pipeline without extra ceremony.
            options.LoggerFactory ??= provider.GetService<ILoggerFactory>();

            return new StormWebSocketServer(options);
        });

        services.AddSingleton<WebSocketHandlerDispatcher>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, StormWebSocketServerHostedService>());
    }

    private static void AddTcpServices(IServiceCollection services)
    {
        services.AddSingleton(provider =>
        {
            TcpServerRegistration registration = provider.GetRequiredService<TcpServerRegistration>();
            ServerOptions options = registration.BuildOptions(provider);
            options.LoggerFactory ??= provider.GetService<ILoggerFactory>();

            return new StormTcpServer(options);
        });

        services.AddSingleton<TcpHandlerDispatcher>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, StormTcpServerHostedService>());
    }
}
