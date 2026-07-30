using System.Text;
using StormSocket.Client;
using StormSocket.Core;
using StormSocket.WebSocket;

namespace StormSocket.Benchmark.Soak;

/// <summary>Counters collected over one phase of the run.</summary>
internal readonly record struct WorkloadTotals(
    long Connections,
    long GracefulClosures,
    long ServerClosures,
    long ResetClosures,
    long Messages,
    long Failures);

/// <summary>
/// Drives the server with the traffic shapes a leak hides in: continuous connection churn, steady
/// traffic on long-lived connections, and teardowns that never complete a closing handshake.
/// </summary>
internal sealed class SoakWorkload
{
    private static readonly TimeSpan EchoTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Gap between churn cycles on a single worker.</summary>
    /// <remarks>
    /// Paced on purpose: an unthrottled connect loop on loopback drains the ephemeral port range,
    /// because a client that closes first keeps its port in TIME_WAIT for a minute. The run would
    /// then fail on connect errors instead of on what it measures.
    /// </remarks>
    private static readonly TimeSpan ChurnPause = TimeSpan.FromMilliseconds(35);

    /// <summary>Gap between messages on a long-lived connection, to keep a CI runner from saturating.</summary>
    private static readonly TimeSpan SteadyPause = TimeSpan.FromMilliseconds(10);

    /// <summary>Fragment size the raw client splits its large message into.</summary>
    private const int FragmentSize = 16 * 1024;

    private static readonly byte[] SmallText = Encoding.ASCII.GetBytes(new string('s', 200));
    private static readonly byte[] MediumText = Encoding.ASCII.GetBytes(new string('m', 8 * 1024));
    private static readonly byte[] LargeText = Encoding.ASCII.GetBytes(new string('l', 192 * 1024));
    private static readonly byte[] SmallBinary = CreateBinary(512);
    private static readonly byte[] MediumBinary = CreateBinary(32 * 1024);
    private static readonly byte[] LargeBinary = CreateBinary(128 * 1024);
    private static readonly byte[] CloseCommand = Encoding.ASCII.GetBytes(SoakServer.CloseCommand);
    private static readonly byte[] AbortCommand = Encoding.ASCII.GetBytes(SoakServer.AbortCommand);

    private readonly int _port;
    private readonly SoakOptions _options;

    private long _cycle;
    private long _connections;
    private long _gracefulClosures;
    private long _serverClosures;
    private long _resetClosures;
    private long _messages;
    private long _failures;

    public SoakWorkload(int port, SoakOptions options)
    {
        _port = port;
        _options = options;
    }

    /// <summary>Counters accumulated since the current phase started.</summary>
    public WorkloadTotals Totals => new(
        Interlocked.Read(ref _connections),
        Interlocked.Read(ref _gracefulClosures),
        Interlocked.Read(ref _serverClosures),
        Interlocked.Read(ref _resetClosures),
        Interlocked.Read(ref _messages),
        Interlocked.Read(ref _failures));

    /// <summary>
    /// Runs every workload for <paramref name="duration"/> and returns once all clients have stopped.
    /// Counters are reset at the start, so each phase reports only its own traffic.
    /// </summary>
    public async Task RunAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        ResetCounters();

        using CancellationTokenSource phaseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        phaseCts.CancelAfter(duration);

        List<Task> workers = new(_options.ChurnWorkers + _options.SteadyConnections);

        for (int i = 0; i < _options.ChurnWorkers; i++)
        {
            workers.Add(Task.Run(() => RunChurnWorkerAsync(phaseCts.Token), CancellationToken.None));
        }

        for (int i = 0; i < _options.SteadyConnections; i++)
        {
            int workerId = i;
            workers.Add(Task.Run(() => RunSteadyWorkerAsync(workerId, phaseCts.Token), CancellationToken.None));
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private async Task RunChurnWorkerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunChurnCycleAsync(Interlocked.Increment(ref _cycle), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _failures);
            }

            try
            {
                await Task.Delay(ChurnPause, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Six cycles in ten close cleanly from the client (two of them over permessage-deflate), two are
    /// closed by the server, and two die on a TCP reset — so every teardown path is walked thousands
    /// of times in a run, not just the happy one.
    /// </summary>
    private Task RunChurnCycleAsync(long cycle, CancellationToken cancellationToken) => (cycle % 10) switch
    {
        0 or 1 or 2 or 3 => ChurnClientClosedAsync(compression: false, cancellationToken),
        4 or 5 => ChurnClientClosedAsync(compression: true, cancellationToken),
        6 => ChurnServerClosedAsync(CloseCommand, cancellationToken),
        7 => ChurnServerClosedAsync(AbortCommand, cancellationToken),
        _ => ChurnResetAsync(cancellationToken),
    };

    private async Task ChurnClientClosedAsync(bool compression, CancellationToken cancellationToken)
    {
        await using StormWebSocketClient client = CreateClient(compression);
        SemaphoreSlim echoes = AttachEchoCounter(client);

        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _connections);

        await client.SendTextAsync(SmallText, cancellationToken).ConfigureAwait(false);
        await WaitForEchoAsync(echoes, cancellationToken).ConfigureAwait(false);

        await client.SendAsync(MediumBinary, cancellationToken).ConfigureAwait(false);
        await WaitForEchoAsync(echoes, cancellationToken).ConfigureAwait(false);

        await client.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _gracefulClosures);
    }

    private async Task ChurnServerClosedAsync(byte[] command, CancellationToken cancellationToken)
    {
        await using StormWebSocketClient client = CreateClient(compression: false);
        SemaphoreSlim echoes = AttachEchoCounter(client);

        TaskCompletionSource disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnDisconnected += _ =>
        {
            disconnected.TrySetResult();
            return ValueTask.CompletedTask;
        };

        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _connections);

        await client.SendTextAsync(SmallText, cancellationToken).ConfigureAwait(false);
        await WaitForEchoAsync(echoes, cancellationToken).ConfigureAwait(false);

        await client.SendTextAsync(command, cancellationToken).ConfigureAwait(false);
        await disconnected.Task.WaitAsync(EchoTimeout, cancellationToken).ConfigureAwait(false);

        Interlocked.Increment(ref _serverClosures);
    }

    private async Task ChurnResetAsync(CancellationToken cancellationToken)
    {
        using RawWebSocketClient raw = new();

        await raw.ConnectAsync(_port, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _connections);

        await raw.SendFragmentedTextAsync(LargeText, FragmentSize, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _messages);

        // Only part of the echo is read, so the reset in Dispose lands while the server is still
        // writing to this connection.
        await raw.DrainAsync(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _resetClosures);
    }

    private async Task RunSteadyWorkerAsync(int workerId, CancellationToken cancellationToken)
    {
        bool compression = workerId % 3 is 0;
        int sequence = workerId;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using StormWebSocketClient client = CreateClient(compression);
                SemaphoreSlim echoes = AttachEchoCounter(client);

                await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _connections);

                while (!cancellationToken.IsCancellationRequested)
                {
                    if (sequence % 2 is 0)
                    {
                        await client.SendTextAsync(NextText(sequence), cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await client.SendAsync(NextBinary(sequence), cancellationToken).ConfigureAwait(false);
                    }

                    await WaitForEchoAsync(echoes, cancellationToken).ConfigureAwait(false);
                    sequence++;

                    await Task.Delay(SteadyPause, cancellationToken).ConfigureAwait(false);
                }

                await client.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                Interlocked.Increment(ref _gracefulClosures);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _failures);
            }
        }
    }

    /// <summary>Rotates payload sizes so small frames and fragmenting-sized messages both stay in flight.</summary>
    private static ReadOnlyMemory<byte> NextText(int sequence) => (sequence % 6) switch
    {
        0 => SmallText,
        2 => MediumText,
        _ => LargeText,
    };

    private static ReadOnlyMemory<byte> NextBinary(int sequence) => (sequence % 6) switch
    {
        1 => SmallBinary,
        3 => MediumBinary,
        _ => LargeBinary,
    };

    private StormWebSocketClient CreateClient(bool compression) => new(new WsClientOptions
    {
        Uri = new Uri($"ws://127.0.0.1:{_port}/soak"),
        ConnectTimeout = TimeSpan.FromSeconds(10),
        CloseTimeout = TimeSpan.FromSeconds(2),
        MaxMessageSize = 4 * 1024 * 1024,
        Socket = new SocketTuningOptions
        {
            NoDelay = true,
        },
        Heartbeat = new HeartbeatOptions
        {
            PingInterval = TimeSpan.Zero,
            AutoPong = true,
        },
        Compression = new WsCompressionOptions
        {
            Enabled = compression,
        },
    });

    private SemaphoreSlim AttachEchoCounter(StormWebSocketClient client)
    {
        // Never disposed on purpose: a client can deliver one last echo while it is being torn down,
        // and releasing a disposed semaphore from that handler would raise a spurious error.
        SemaphoreSlim echoes = new(0);

        client.OnMessageReceived += _ =>
        {
            Interlocked.Increment(ref _messages);
            echoes.Release();
            return ValueTask.CompletedTask;
        };

        return echoes;
    }

    private static async Task WaitForEchoAsync(SemaphoreSlim echoes, CancellationToken cancellationToken)
    {
        if (!await echoes.WaitAsync(EchoTimeout, cancellationToken).ConfigureAwait(false))
        {
            throw new TimeoutException("The server did not echo the message within the timeout.");
        }
    }

    private static byte[] CreateBinary(int size)
    {
        // Seeded rather than random: the same bytes every run keeps compression ratios, and therefore
        // the traffic volume, comparable between runs.
        byte[] payload = new byte[size];
        new Random(20260730).NextBytes(payload);
        return payload;
    }

    private void ResetCounters()
    {
        Interlocked.Exchange(ref _connections, 0);
        Interlocked.Exchange(ref _gracefulClosures, 0);
        Interlocked.Exchange(ref _serverClosures, 0);
        Interlocked.Exchange(ref _resetClosures, 0);
        Interlocked.Exchange(ref _messages, 0);
        Interlocked.Exchange(ref _failures, 0);
    }
}
