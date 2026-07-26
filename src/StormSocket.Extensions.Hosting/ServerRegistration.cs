using StormSocket.Server;

namespace StormSocket.Extensions.Hosting;

/// <summary>
/// What the builder collected before the container was built, applied when the server is created.
/// </summary>
/// <remarks>
/// Configuration is kept as a list of delegates rather than a filled-in options object so that
/// several calls compose in registration order, the way <c>IOptions</c> configuration does.
/// </remarks>
internal abstract class ServerRegistration
{
    // Every step is stored in the provider-aware shape so that a step which needs a service and one
    // which does not still run in the single order they were registered in.
    private readonly List<Action<ServerOptions, IServiceProvider>> _configurations = [];

    /// <summary>True when at least one handler is registered with a lifetime other than singleton.</summary>
    public bool RequiresScope { get; private set; }

    public void AddConfiguration(Action<ServerOptions> configure)
        => _configurations.Add((options, _) => configure(options));

    public void AddConfiguration(Action<ServerOptions, IServiceProvider> configure)
        => _configurations.Add(configure);

    public void RequireScope() => RequiresScope = true;

    public ServerOptions BuildOptions(IServiceProvider services)
    {
        ServerOptions options = CreateDefaults();

        foreach (Action<ServerOptions, IServiceProvider> configure in _configurations)
        {
            configure(options, services);
        }

        return options;
    }

    protected abstract ServerOptions CreateDefaults();
}

internal sealed class WebSocketServerRegistration : ServerRegistration
{
    // A WebSocketOptions instance is created up front so that `options.WebSocket.MaxFrameSize = ...`
    // works inside a configuration delegate without the caller having to construct one first.
    protected override ServerOptions CreateDefaults() => new() { WebSocket = new WebSocketOptions() };
}

internal sealed class TcpServerRegistration : ServerRegistration
{
    protected override ServerOptions CreateDefaults() => new();
}
