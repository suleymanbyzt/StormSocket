using StormSocket.Client;

// Echo client for the Autobahn Testsuite's fuzzingserver mode, which tests the client half of the
// protocol: the suite plays the server and sends malformed frames, bad UTF-8 and hostile close
// bodies, judging what the client does with them.
//
// The protocol the suite expects is three plain WebSocket connections: one to learn the case count,
// one per case, and one at the end to make it write its report.
string host = args.Length > 0 ? args[0] : "localhost";
int port = args.Length > 1 ? int.Parse(args[1]) : 9001;
const string Agent = "StormSocket";

string baseUri = $"ws://{host}:{port}";

int caseCount = await GetCaseCountAsync(baseUri);
Console.WriteLine($"Autobahn reports {caseCount} cases");

for (int caseNumber = 1; caseNumber <= caseCount; caseNumber++)
{
    await RunCaseAsync(baseUri, caseNumber, caseCount);
}

await UpdateReportsAsync(baseUri);
Console.WriteLine("Reports updated");

static async Task<int> GetCaseCountAsync(string baseUri)
{
    TaskCompletionSource<int> count = new(TaskCreationOptions.RunContinuationsAsynchronously);

    await using StormWebSocketClient client = new(new WsClientOptions
    {
        Uri = new Uri($"{baseUri}/getCaseCount"),
        Heartbeat = new() { PingInterval = TimeSpan.Zero },
    });

    client.OnMessageReceived += message =>
    {
        if (message.IsText && int.TryParse(message.Text, out int parsed))
        {
            count.TrySetResult(parsed);
        }

        return ValueTask.CompletedTask;
    };

    await client.ConnectAsync();
    return await count.Task.WaitAsync(TimeSpan.FromSeconds(30));
}

static async Task RunCaseAsync(string baseUri, int caseNumber, int caseCount)
{
    StormWebSocketClient client = new(new WsClientOptions
    {
        Uri = new Uri($"{baseUri}/runCase?case={caseNumber}&agent={Uri.EscapeDataString(Agent)}"),
        Heartbeat = new() { PingInterval = TimeSpan.Zero },

        // Section 9 sends 16 MB payloads and expects them echoed rather than rejected.
        MaxFrameSize = 20 * 1024 * 1024,
        MaxMessageSize = 20 * 1024 * 1024,
        Compression = new() { Enabled = true },
    });

    TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    client.OnMessageReceived += async message =>
    {
        // A faithful echo: text back as text, binary back as binary. Anything else would measure this
        // harness rather than the library.
        if (message.IsText)
        {
            await client.SendTextAsync(message.Data);
        }
        else
        {
            await client.SendAsync(message.Data);
        }
    };

    client.OnDisconnected += _ =>
    {
        finished.TrySetResult();
        return ValueTask.CompletedTask;
    };

    client.OnError += _ =>
    {
        // Failing a connection is the correct outcome for most cases in this suite, so an error here
        // is data, not a problem: the case ends and the next one starts.
        finished.TrySetResult();
        return ValueTask.CompletedTask;
    };

    try
    {
        await client.ConnectAsync();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"case {caseNumber}/{caseCount}: {ex.GetType().Name}");
    }
    finally
    {
        await client.DisposeAsync();
    }

    if (caseNumber % 25 == 0 || caseNumber == caseCount)
    {
        Console.WriteLine($"  {caseNumber}/{caseCount}");
    }
}

static async Task UpdateReportsAsync(string baseUri)
{
    await using StormWebSocketClient client = new(new WsClientOptions
    {
        Uri = new Uri($"{baseUri}/updateReports?agent={Uri.EscapeDataString(Agent)}"),
        Heartbeat = new() { PingInterval = TimeSpan.Zero },
    });

    TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    client.OnDisconnected += _ =>
    {
        finished.TrySetResult();
        return ValueTask.CompletedTask;
    };

    await client.ConnectAsync();
    await finished.Task.WaitAsync(TimeSpan.FromSeconds(60));
}
