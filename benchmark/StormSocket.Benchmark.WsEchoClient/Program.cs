using StormSocket.Client;

string address = "127.0.0.1";
int port = 8080;
int clientCount = 100;
int messages = 1000;
int size = 32;
int seconds = 10;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-a" or "--address":
            address = args[++i];
            break;
        case "-p" or "--port":
            port = int.Parse(args[++i]);
            break;
        case "-c" or "--clients":
            clientCount = int.Parse(args[++i]);
            break;
        case "-m" or "--messages":
            messages = int.Parse(args[++i]);
            break;
        case "-s" or "--size":
            size = int.Parse(args[++i]);
            break;
        case "-z" or "--seconds":
            seconds = int.Parse(args[++i]);
            break;
    }
}

Console.WriteLine($"Server address: {address}");
Console.WriteLine($"Server port: {port}");
Console.WriteLine($"Working clients: {clientCount}");
Console.WriteLine($"Working messages: {messages}");
Console.WriteLine($"Message size: {size}");
Console.WriteLine($"Seconds to benchmarking: {seconds}");
Console.WriteLine();

byte[] messageToSend = new byte[size];
long totalErrors = 0;
long totalBytes = 0;
DateTime timestampStart = DateTime.UtcNow;
DateTime timestampStop = DateTime.UtcNow;

// Create echo clients
List<StormWebSocketClient> clients = [];
for (int i = 0; i < clientCount; i++)
{
    StormWebSocketClient client = new StormWebSocketClient(new WsClientOptions
    {
        Uri = new Uri($"ws://{address}:{port}/"),
        NoDelay = true,
        PingInterval = TimeSpan.Zero,
    });

    long received = 0;

    client.OnConnected += async () =>
    {
        for (int j = 0; j < messages; j++)
        {
            await client.SendAsync(messageToSend);
        }
    };

    client.OnMessageReceived += async msg =>
    {
        received += msg.Data.Length;
        Interlocked.Add(ref totalBytes, msg.Data.Length);
        timestampStop = DateTime.UtcNow;

        while (received >= size)
        {
            try
            {
                await client.SendAsync(messageToSend);
            }
            catch
            {
                break;
            }

            received -= size;
        }
    };

    client.OnError += async ex =>
    {
        Interlocked.Increment(ref totalErrors);
        await ValueTask.CompletedTask;
    };

    clients.Add(client);
}

timestampStart = DateTime.UtcNow;

// Connect clients
Console.Write("Clients connecting...");
List<Task> connectTasks = [];
foreach (StormWebSocketClient client in clients)
{
    connectTasks.Add(client.ConnectAsync());
}

await Task.WhenAll(connectTasks);
Console.WriteLine("Done!");
Console.WriteLine("All clients connected!");

// Wait for benchmarking
Console.Write("Benchmarking...");
await Task.Delay(seconds * 1000);
Console.WriteLine("Done!");

// Disconnect clients
Console.Write("Clients disconnecting...");
List<Task> disconnectTasks = [];
foreach (StormWebSocketClient client in clients)
{
    disconnectTasks.Add(client.DisconnectAsync());
}

await Task.WhenAll(disconnectTasks);
Console.WriteLine("Done!");
Console.WriteLine("All clients disconnected!");

Console.WriteLine();
Console.WriteLine($"Errors: {totalErrors}");
Console.WriteLine();

long totalMessages = totalBytes / size;
double totalTime = (timestampStop - timestampStart).TotalMilliseconds;

Console.WriteLine($"Total time: {FormatTime(totalTime)}");
Console.WriteLine($"Total data: {FormatData(totalBytes)}");
Console.WriteLine($"Total messages: {totalMessages}");
Console.WriteLine($"Data throughput: {FormatData((long)(totalBytes / (totalTime / 1000.0)))}/s");
if (totalMessages > 0)
{
    Console.WriteLine($"Message latency: {FormatTime(totalTime / totalMessages)}");
    Console.WriteLine($"Message throughput: {(long)(totalMessages / (totalTime / 1000.0))} msg/s");
}

// Cleanup
foreach (StormWebSocketClient client in clients)
{
    await client.DisposeAsync();
}

static string FormatTime(double ms)
{
    if (ms >= 1000.0)
        return $"{ms / 1000.0:F3} s";
    if (ms >= 1.0)
        return $"{ms:F3} ms";
    if (ms >= 0.001)
        return $"{ms * 1000.0:F3} us";
    return $"{ms * 1000000.0:F3} ns";
}

static string FormatData(long bytes)
{
    if (bytes >= 1024L * 1024 * 1024)
        return $"{bytes / (1024.0 * 1024 * 1024):F3} GiB";
    if (bytes >= 1024L * 1024)
        return $"{bytes / (1024.0 * 1024):F3} MiB";
    if (bytes >= 1024L)
        return $"{bytes / 1024.0:F3} KiB";
    return $"{bytes} bytes";
}
