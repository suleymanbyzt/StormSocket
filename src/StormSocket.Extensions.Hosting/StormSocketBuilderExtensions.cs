using System.Net;
using Microsoft.Extensions.DependencyInjection;
using StormSocket.Middleware;
using StormSocket.Server;

namespace StormSocket.Extensions.Hosting;

/// <summary>
/// Adds handlers, middleware and endpoints to a registered server.
/// </summary>
/// <remarks>
/// The methods are declared once per builder type rather than once over a generic type parameter, so
/// that calls chain without the caller ever naming a type argument.
/// </remarks>
public static class StormSocketBuilderExtensions
{
    /// <summary>
    /// Registers a handler for the WebSocket server's connections and messages.
    /// </summary>
    /// <param name="lifetime">
    /// <see cref="ServiceLifetime.Scoped"/> by default, so the handler and its dependencies are
    /// resolved per invocation and a scoped dependency such as a <c>DbContext</c> behaves the way it
    /// does in a web request. Use <see cref="ServiceLifetime.Singleton"/> on a hot path to avoid the
    /// scope: the handler is then resolved once and must be safe to use concurrently.
    /// </param>
    /// <remarks>Call more than once to run several handlers; they are invoked in registration order.</remarks>
    public static IStormWebSocketServerBuilder AddHandler<THandler>(
        this IStormWebSocketServerBuilder builder,
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where THandler : class, IWebSocketHandler
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.Add(new ServiceDescriptor(typeof(IWebSocketHandler), typeof(THandler), lifetime));

        if (lifetime != ServiceLifetime.Singleton)
        {
            GetRegistration<WebSocketServerRegistration>(builder).RequireScope();
        }

        return builder;
    }

    /// <inheritdoc cref="AddHandler{THandler}(IStormWebSocketServerBuilder, ServiceLifetime)"/>
    public static IStormTcpServerBuilder AddHandler<THandler>(
        this IStormTcpServerBuilder builder,
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where THandler : class, ITcpConnectionHandler
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.Add(new ServiceDescriptor(typeof(ITcpConnectionHandler), typeof(THandler), lifetime));

        if (lifetime != ServiceLifetime.Singleton)
        {
            GetRegistration<TcpServerRegistration>(builder).RequireScope();
        }

        return builder;
    }

    /// <summary>
    /// Registers a middleware, resolved from the container, that intercepts the connection lifecycle
    /// and data flow before handlers see it.
    /// </summary>
    /// <remarks>
    /// Middleware is per server and resolved once, so it must be safe to use concurrently. Register
    /// an instance directly (<c>services.AddSingleton&lt;IConnectionMiddleware&gt;(new RateLimitMiddleware(...))</c>)
    /// when it needs constructor arguments the container cannot supply.
    /// </remarks>
    public static IStormWebSocketServerBuilder UseMiddleware<TMiddleware>(this IStormWebSocketServerBuilder builder)
        where TMiddleware : class, IConnectionMiddleware
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<IConnectionMiddleware, TMiddleware>();
        return builder;
    }

    /// <inheritdoc cref="UseMiddleware{TMiddleware}(IStormWebSocketServerBuilder)"/>
    public static IStormTcpServerBuilder UseMiddleware<TMiddleware>(this IStormTcpServerBuilder builder)
        where TMiddleware : class, IConnectionMiddleware
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<IConnectionMiddleware, TMiddleware>();
        return builder;
    }

    /// <summary>Listens on the given address and port.</summary>
    public static IStormWebSocketServerBuilder ListenOn(this IStormWebSocketServerBuilder builder, IPAddress address, int port)
        => builder.Configure(ListenAction(address, port));

    /// <inheritdoc cref="ListenOn(IStormWebSocketServerBuilder, IPAddress, int)"/>
    public static IStormTcpServerBuilder ListenOn(this IStormTcpServerBuilder builder, IPAddress address, int port)
        => builder.Configure(ListenAction(address, port));

    /// <summary>Listens on every interface, on the given port.</summary>
    public static IStormWebSocketServerBuilder ListenOnAnyIP(this IStormWebSocketServerBuilder builder, int port)
        => builder.ListenOn(IPAddress.Any, port);

    /// <inheritdoc cref="ListenOnAnyIP(IStormWebSocketServerBuilder, int)"/>
    public static IStormTcpServerBuilder ListenOnAnyIP(this IStormTcpServerBuilder builder, int port)
        => builder.ListenOn(IPAddress.Any, port);

    /// <summary>Adds another configuration step, applied after the ones registered before it.</summary>
    public static IStormWebSocketServerBuilder Configure(this IStormWebSocketServerBuilder builder, Action<ServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        GetRegistration<WebSocketServerRegistration>(builder).AddConfiguration(configure);
        return builder;
    }

    /// <inheritdoc cref="Configure(IStormWebSocketServerBuilder, Action{ServerOptions})"/>
    public static IStormTcpServerBuilder Configure(this IStormTcpServerBuilder builder, Action<ServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        GetRegistration<TcpServerRegistration>(builder).AddConfiguration(configure);
        return builder;
    }

    private static Action<ServerOptions> ListenAction(IPAddress address, int port)
    {
        ArgumentNullException.ThrowIfNull(address);
        return options => options.EndPoint = new IPEndPoint(address, port);
    }

    private static TRegistration GetRegistration<TRegistration>(IStormSocketServerBuilder builder)
        where TRegistration : ServerRegistration
    {
        foreach (ServiceDescriptor descriptor in builder.Services)
        {
            if (descriptor.ServiceType == typeof(TRegistration) && descriptor.ImplementationInstance is TRegistration registration)
            {
                return registration;
            }
        }

        throw new InvalidOperationException(
            $"No {typeof(TRegistration).Name} was found. Call AddStormWebSocketServer or AddStormTcpServer first.");
    }
}
