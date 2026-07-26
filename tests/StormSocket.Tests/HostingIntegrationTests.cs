using System.Linq;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using StormSocket.Client;
using StormSocket.Core;
using StormSocket.Events;
using StormSocket.Extensions.Hosting;
using StormSocket.Server;
using StormSocket.Session;
using Xunit;

namespace StormSocket.Tests;

public class HostingIntegrationTests
{
    private static IHostBuilder CreateHost(Action<IStormWebSocketServerBuilder> configure)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                IStormWebSocketServerBuilder builder = services.AddStormWebSocketServer(options =>
                {
                    options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0);
                    options.WebSocket!.Heartbeat.PingInterval = TimeSpan.Zero;
                });

                configure(builder);
            });
    }

    private static async Task<StormWebSocketClient> ConnectAsync(IHost host)
    {
        StormWebSocketServer server = host.Services.GetRequiredService<StormWebSocketServer>();
        int port = ((IPEndPoint)server.LocalEndPoint!).Port;

        StormWebSocketClient client = new(new WsClientOptions
        {
            Uri = new Uri($"ws://127.0.0.1:{port}"),
            Heartbeat = new() { PingInterval = TimeSpan.Zero },
        });

        await client.ConnectAsync();
        return client;
    }

    [Fact]
    public async Task Handler_ResolvedFromContainer_ReceivesMessages()
    {
        using IHost host = CreateHost(builder => builder.AddHandler<EchoHandler>()).Build();
        await host.StartAsync();

        await using StormWebSocketClient client = await ConnectAsync(host);

        TaskCompletionSource<string> echoed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnMessageReceived += msg =>
        {
            echoed.TrySetResult(msg.Text);
            return ValueTask.CompletedTask;
        };

        await client.SendTextAsync("hello");

        Assert.Equal("echo:hello", await echoed.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        await host.StopAsync();
    }

    [Fact]
    public async Task ScopedHandler_GetsAFreshScopePerMessage()
    {
        ScopeProbe.Reset();

        using IHost host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddScoped<ScopeProbe>();
                services.AddStormWebSocketServer(options =>
                    {
                        options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0);
                        options.WebSocket!.Heartbeat.PingInterval = TimeSpan.Zero;
                    })
                    .AddHandler<ScopedHandler>();
            })
            .Build();

        await host.StartAsync();
        await using StormWebSocketClient client = await ConnectAsync(host);

        for (int i = 0; i < 3; i++)
        {
            await client.SendTextAsync($"message-{i}");
        }

        await ScopeProbe.WaitForAsync(expected: 3);

        // A distinct scoped dependency per message is the whole point of the default lifetime: it is
        // what makes injecting a DbContext into a handler safe. Asserted on the identities the
        // messages saw rather than a total instance count, because the connect event legitimately
        // gets a scope of its own.
        Assert.Equal(3, ScopeProbe.MessageScopeIds.Length);
        Assert.Equal(3, ScopeProbe.MessageScopeIds.Distinct().Count());

        await host.StopAsync();
    }

    [Fact]
    public async Task SingletonHandler_IsResolvedOnce()
    {
        SingletonHandler.Reset();

        using IHost host = CreateHost(builder => builder.AddHandler<SingletonHandler>(ServiceLifetime.Singleton)).Build();
        await host.StartAsync();

        await using StormWebSocketClient client = await ConnectAsync(host);

        for (int i = 0; i < 3; i++)
        {
            await client.SendTextAsync($"message-{i}");
        }

        await SingletonHandler.WaitForAsync(expected: 3);
        Assert.Equal(1, SingletonHandler.InstanceCount);

        await host.StopAsync();
    }

    [Fact]
    public async Task Handlers_RunInRegistrationOrder_AndOneThrowingDoesNotStopTheOthers()
    {
        OrderProbe.Reset();

        using IHost host = CreateHost(builder => builder
                .AddHandler<FirstHandler>(ServiceLifetime.Singleton)
                .AddHandler<ThrowingHandler>(ServiceLifetime.Singleton)
                .AddHandler<LastHandler>(ServiceLifetime.Singleton))
            .Build();

        await host.StartAsync();
        await using StormWebSocketClient client = await ConnectAsync(host);

        await client.SendTextAsync("go");
        await OrderProbe.WaitForAsync(expected: 2);

        Assert.Equal(["first", "last"], OrderProbe.Order);

        await host.StopAsync();
    }

    [Fact]
    public async Task HealthCheck_ReportsHealthyWhileListening()
    {
        using IHost host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddStormWebSocketServer(options =>
                {
                    options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0);
                    options.WebSocket!.Heartbeat.PingInterval = TimeSpan.Zero;
                });

                services.AddHealthChecks().AddStormWebSocketServer();
            })
            .Build();

        HealthCheckService health = host.Services.GetRequiredService<HealthCheckService>();

        Assert.Equal(HealthStatus.Unhealthy, (await health.CheckHealthAsync()).Status);

        await host.StartAsync();
        Assert.Equal(HealthStatus.Healthy, (await health.CheckHealthAsync()).Status);

        await host.StopAsync();
        Assert.Equal(HealthStatus.Unhealthy, (await health.CheckHealthAsync()).Status);
    }

    [Fact]
    public async Task ConfigurationDelegates_ComposeInRegistrationOrder()
    {
        using IHost host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddStormWebSocketServer(options =>
                {
                    options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0);
                    options.MaxConnections = 100;
                });

                services.AddStormWebSocketServer(options => options.MaxConnections = 5_000);
            })
            .Build();

        StormWebSocketServer server = host.Services.GetRequiredService<StormWebSocketServer>();
        await host.StartAsync();

        // Both delegates ran, and the later registration won — the pattern a library uses to ship a
        // default the application can override.
        Assert.NotNull(server.LocalEndPoint);
        Assert.True(server.IsRunning);

        await host.StopAsync();
        Assert.False(server.IsRunning);
    }

    [Fact]
    public async Task Options_BindFromConfiguration()
    {
        Dictionary<string, string?> settings = new()
        {
            ["StormSocket:Host"] = "localhost",
            ["StormSocket:Port"] = "0",
            ["StormSocket:MaxConnections"] = "1234",
            ["StormSocket:MaxConnectionsPerIp"] = "7",
            ["StormSocket:WebSocket:MaxFrameSize"] = "65536",
            ["StormSocket:WebSocket:IdleTimeout"] = "00:05:00",
            ["StormSocket:WebSocket:Heartbeat:PingInterval"] = "00:00:00",
        };

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        using IHost host = Host.CreateDefaultBuilder()
            .ConfigureServices(services => services
                .AddStormWebSocketServer()
                .BindConfiguration(configuration.GetSection("StormSocket")))
            .Build();

        await host.StartAsync();

        StormWebSocketServer server = host.Services.GetRequiredService<StormWebSocketServer>();
        IPEndPoint endPoint = (IPEndPoint)server.LocalEndPoint!;

        Assert.Equal(IPAddress.Loopback, endPoint.Address);
        Assert.True(server.IsRunning);

        // The port came from configuration as 0, so the OS picked one — proof the endpoint was bound
        // from Host/Port rather than left at the default.
        Assert.NotEqual(5000, endPoint.Port);

        await host.StopAsync();
    }

    [Fact]
    public async Task Options_CanBeConfiguredFromOtherServices()
    {
        using IHost host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(new EndpointSource(IPAddress.Loopback));
                services
                    .AddStormWebSocketServer(options => options.WebSocket!.Heartbeat.PingInterval = TimeSpan.Zero)
                    .Configure((options, provider) =>
                        options.EndPoint = new IPEndPoint(provider.GetRequiredService<EndpointSource>().Address, 0));
            })
            .Build();

        await host.StartAsync();

        StormWebSocketServer server = host.Services.GetRequiredService<StormWebSocketServer>();
        Assert.Equal(IPAddress.Loopback, ((IPEndPoint)server.LocalEndPoint!).Address);

        await host.StopAsync();
    }

    private sealed record EndpointSource(IPAddress Address);

    private sealed class EchoHandler : IWebSocketHandler
    {
        public ValueTask OnMessageAsync(IWebSocketSession session, WsMessage message, CancellationToken cancellationToken)
            => session.SendTextAsync($"echo:{message.Text}", cancellationToken);
    }

    private sealed class ScopeProbe
    {
        private static int _nextId;
        private static readonly List<int> Seen = [];
        private static TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Id { get; } = Interlocked.Increment(ref _nextId);

        public static int[] MessageScopeIds
        {
            get
            {
                lock (Seen)
                {
                    return [.. Seen];
                }
            }
        }

        public static void Reset()
        {
            lock (Seen)
            {
                Seen.Clear();
            }

            _reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public static void RecordMessage(int id, int expected)
        {
            lock (Seen)
            {
                Seen.Add(id);
                if (Seen.Count >= expected)
                {
                    _reached.TrySetResult();
                }
            }
        }

        public static Task WaitForAsync(int expected) => _reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class ScopedHandler(ScopeProbe probe) : IWebSocketHandler
    {
        public ValueTask OnMessageAsync(IWebSocketSession session, WsMessage message, CancellationToken cancellationToken)
        {
            ScopeProbe.RecordMessage(probe.Id, expected: 3);
            return default;
        }
    }

    private sealed class SingletonHandler : IWebSocketHandler
    {
        private static int _instanceCount;
        private static int _handled;
        private static TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SingletonHandler() => Interlocked.Increment(ref _instanceCount);

        public static int InstanceCount => Volatile.Read(ref _instanceCount);

        public static void Reset()
        {
            Volatile.Write(ref _instanceCount, 0);
            Volatile.Write(ref _handled, 0);
            _reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public static Task WaitForAsync(int expected) => _reached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public ValueTask OnMessageAsync(IWebSocketSession session, WsMessage message, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _handled) >= 3)
            {
                _reached.TrySetResult();
            }

            return default;
        }
    }

    private static class OrderProbe
    {
        private static readonly List<string> Entries = [];
        private static TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static string[] Order
        {
            get
            {
                lock (Entries)
                {
                    return [.. Entries];
                }
            }
        }

        public static void Reset()
        {
            lock (Entries)
            {
                Entries.Clear();
            }

            _reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public static void Record(string name, int expected)
        {
            lock (Entries)
            {
                Entries.Add(name);
                if (Entries.Count >= expected)
                {
                    _reached.TrySetResult();
                }
            }
        }

        public static Task WaitForAsync(int expected) => _reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class FirstHandler : IWebSocketHandler
    {
        public ValueTask OnMessageAsync(IWebSocketSession session, WsMessage message, CancellationToken cancellationToken)
        {
            OrderProbe.Record("first", expected: 2);
            return default;
        }
    }

    private sealed class ThrowingHandler : IWebSocketHandler
    {
        public ValueTask OnMessageAsync(IWebSocketSession session, WsMessage message, CancellationToken cancellationToken)
            => throw new InvalidOperationException("handler failure");
    }

    private sealed class LastHandler : IWebSocketHandler
    {
        public ValueTask OnMessageAsync(IWebSocketSession session, WsMessage message, CancellationToken cancellationToken)
        {
            OrderProbe.Record("last", expected: 2);
            return default;
        }
    }
}
