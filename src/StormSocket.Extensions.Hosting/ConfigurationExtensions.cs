using System.Net;
using Microsoft.Extensions.Configuration;
using StormSocket.Server;

namespace StormSocket.Extensions.Hosting;

/// <summary>
/// Binds server options from configuration, and configures them with access to other services.
/// </summary>
public static class ConfigurationExtensions
{
    /// <summary>
    /// Binds the server's options from a configuration section.
    /// </summary>
    /// <remarks>
    /// Everything on <see cref="ServerOptions"/> binds by name, including the nested
    /// <c>WebSocket</c>, <c>Socket</c> and <c>Heartbeat</c> sections. The endpoint is the exception:
    /// <see cref="EndPoint"/> is an abstract type that configuration cannot construct, so it is
    /// expressed as <c>Host</c> and <c>Port</c> keys instead.
    /// <example>
    /// <code language="json">
    /// {
    ///   "StormSocket": {
    ///     "Port": 8080,
    ///     "Host": "0.0.0.0",
    ///     "MaxConnections": 10000,
    ///     "MaxConnectionsPerIp": 20,
    ///     "WebSocket": {
    ///       "MaxFrameSize": 1048576,
    ///       "IdleTimeout": "00:05:00",
    ///       "Heartbeat": { "PingInterval": "00:00:20" }
    ///     }
    ///   }
    /// }
    /// </code>
    /// </example>
    /// </remarks>
    public static IStormWebSocketServerBuilder BindConfiguration(this IStormWebSocketServerBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        return builder.Configure(options => Bind(options, configuration));
    }

    /// <inheritdoc cref="BindConfiguration(IStormWebSocketServerBuilder, IConfiguration)"/>
    public static IStormTcpServerBuilder BindConfiguration(this IStormTcpServerBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        return builder.Configure(options => Bind(options, configuration));
    }

    /// <summary>
    /// Adds a configuration step that can resolve services, for options that are not known until the
    /// container is built — a certificate from a secret store, an endpoint from a discovery service.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services
    ///     .AddStormWebSocketServer()
    ///     .Configure((options, services) =&gt;
    ///     {
    ///         options.Ssl = new SslOptions { Certificate = services.GetRequiredService&lt;ICertificateStore&gt;().Current };
    ///     });
    /// </code>
    /// </example>
    public static IStormWebSocketServerBuilder Configure(
        this IStormWebSocketServerBuilder builder,
        Action<ServerOptions, IServiceProvider> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        GetRegistration<WebSocketServerRegistration>(builder).AddConfiguration(configure);
        return builder;
    }

    /// <inheritdoc cref="Configure(IStormWebSocketServerBuilder, Action{ServerOptions, IServiceProvider})"/>
    public static IStormTcpServerBuilder Configure(
        this IStormTcpServerBuilder builder,
        Action<ServerOptions, IServiceProvider> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        GetRegistration<TcpServerRegistration>(builder).AddConfiguration(configure);
        return builder;
    }

    private static void Bind(ServerOptions options, IConfiguration configuration)
    {
        configuration.Bind(options);

        string? port = configuration["Port"];
        if (port is null)
        {
            return;
        }

        if (!int.TryParse(port, out int parsedPort))
        {
            throw new InvalidOperationException($"Configuration value '{PathOf(configuration)}:Port' is not a number: '{port}'.");
        }

        string? host = configuration["Host"];
        IPAddress address = host is null ? IPAddress.Any : ParseHost(host, PathOf(configuration));

        options.EndPoint = new IPEndPoint(address, parsedPort);
    }

    /// <summary>The section's path when there is one, so an error message can point at the key.</summary>
    private static string PathOf(IConfiguration configuration)
        => configuration is IConfigurationSection section ? section.Path : "StormSocket";

    private static IPAddress ParseHost(string host, string path)
    {
        // "any" and "localhost" are spelled out because they are what people reach for in a config
        // file, and neither parses as an address.
        if (host.Equals("any", StringComparison.OrdinalIgnoreCase) || host == "*")
        {
            return IPAddress.Any;
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        return IPAddress.TryParse(host, out IPAddress? address)
            ? address
            : throw new InvalidOperationException($"Configuration value '{path}:Host' is not an IP address: '{host}'.");
    }

    private static TRegistration GetRegistration<TRegistration>(IStormSocketServerBuilder builder)
        where TRegistration : ServerRegistration
    {
        foreach (Microsoft.Extensions.DependencyInjection.ServiceDescriptor descriptor in builder.Services)
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
