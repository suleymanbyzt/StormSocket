using Microsoft.Extensions.DependencyInjection;

namespace StormSocket.Extensions.Hosting;

/// <summary>
/// Configures a StormSocket server that has been added to a service collection.
/// </summary>
/// <remarks>
/// Kept deliberately thin: it carries the service collection so that extension methods, including
/// ones outside this library, can add to the registration without this type having to know about them.
/// </remarks>
public interface IStormSocketServerBuilder
{
    /// <summary>The service collection the server was registered in.</summary>
    IServiceCollection Services { get; }
}

/// <summary>Configures a registered <see cref="StormSocket.Server.StormWebSocketServer"/>.</summary>
public interface IStormWebSocketServerBuilder : IStormSocketServerBuilder
{
}

/// <summary>Configures a registered <see cref="StormSocket.Server.StormTcpServer"/>.</summary>
public interface IStormTcpServerBuilder : IStormSocketServerBuilder
{
}

internal sealed class StormWebSocketServerBuilder(IServiceCollection services) : IStormWebSocketServerBuilder
{
    public IServiceCollection Services { get; } = services;
}

internal sealed class StormTcpServerBuilder(IServiceCollection services) : IStormTcpServerBuilder
{
    public IServiceCollection Services { get; } = services;
}
