using System.Text;
using System.Text.Json;
using StormSocket.Server;
using StormSocket.Session;

namespace StormSocket.Samples.WsServer.Services;

/// <summary>
/// Broadcasts a heartbeat message to all connected clients at a fixed interval.
/// Each tick includes a server timestamp and online user count.
/// </summary>
public sealed class TickerService : IAsyncDisposable
{
    private readonly StormWebSocketServer _server;
    private readonly UserManager _users;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();
    private Task? _task;

    public TickerService(StormWebSocketServer server, UserManager users, TimeSpan interval)
    {
        _server = server;
        _users = users;
        _interval = interval;
    }

    public void Start()
    {
        _task = RunAsync(_cts.Token);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using PeriodicTimer timer = new(_interval);

        try
        {
            long tick = 0;
            while (await timer.WaitForNextTickAsync(ct))
            {
                tick++;

                string json = JsonSerializer.Serialize(new
                {
                    type = "heartbeat",
                    tick,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    utc = DateTimeOffset.UtcNow.ToString("o"),
                    online = _server.Sessions.Count,
                });

                byte[] bytes = Encoding.UTF8.GetBytes(json);

                foreach (ISession session in _server.Sessions.All)
                {
                    if (session is WebSocketSession ws)
                    {
                        try
                        {
                            await ws.SendTextAsync(bytes, ct);
                        }
                        catch
                        {
                            // ignored
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
#if NET8_0_OR_GREATER
        await _cts.CancelAsync();
#else
        _cts.Cancel();
#endif

        if (_task is not null)
        {
            try
            {
                await _task;
            }
            catch
            {
                // ignored
            }
        }

        _cts.Dispose();
    }
}