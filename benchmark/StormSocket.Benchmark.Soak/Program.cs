using System.Diagnostics;
using System.Globalization;

namespace StormSocket.Benchmark.Soak;

/// <summary>
/// Runs the library under load for a bounded time and then checks that everything it took came back:
/// the managed heap, the process descriptors, and the server's own view of its sessions and groups.
/// Exits non-zero with the offending number when a limit is exceeded.
/// </summary>
internal static class Program
{
    /// <summary>How long the drain waits for the server to unwind after the last client is gone.</summary>
    private const int DrainTimeoutSeconds = 30;

    /// <summary>Connections a phase must have opened before its measurements mean anything.</summary>
    /// <remarks>
    /// The slowest plausible runner still manages a few connections per second, and a short manual
    /// run (<c>--seconds 20</c>) clears this by two orders of magnitude.
    /// </remarks>
    private const int MinimumConnections = 200;

    /// <summary>Failed connections are tolerated up to one in this many.</summary>
    private const int FailureRateDivisor = 20;

    /// <summary>
    /// Idle window before each measurement.
    /// </summary>
    /// <remarks>
    /// Teardown finishes off the workload's threads — finalizers, pooled pipe segments returned by
    /// the connection loops — so both the baseline and the final reading are taken after the same
    /// quiet period, which keeps the difference between them meaningful.
    /// </remarks>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(3);

    private static async Task<int> Main(string[] args)
    {
        // Numbers end up in a job summary that people compare across runs, so they must not change
        // shape with the runner's locale.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

        if (!SoakOptions.TryParse(args, out SoakOptions options, out string? error))
        {
            if (!string.IsNullOrEmpty(error))
            {
                Console.Error.WriteLine(error);
            }

            Console.Error.WriteLine(SoakOptions.Usage);
            return string.IsNullOrEmpty(error) ? 0 : 2;
        }

        Stopwatch wallClock = Stopwatch.StartNew();

        await using SoakServer server = new();
        await server.StartAsync().ConfigureAwait(false);

        SoakWorkload workload = new(server.Port, options);

        Console.WriteLine($"StormSocket resource soak — warmup {options.WarmupSeconds}s, measured {options.Seconds}s, port {server.Port}");
        Console.WriteLine("Running...");

        await workload.RunAsync(TimeSpan.FromSeconds(options.WarmupSeconds)).ConfigureAwait(false);
        bool warmupDrained = await WaitForDrainAsync(server).ConfigureAwait(false);
        await Task.Delay(SettleDelay).ConfigureAwait(false);

        ResourceSnapshot baseline = ResourceSnapshot.Capture();
        long serverErrorsBefore = server.ErrorCount;
        long serverMessagesBefore = server.MessagesReceived;

        await workload.RunAsync(TimeSpan.FromSeconds(options.Seconds)).ConfigureAwait(false);
        WorkloadTotals totals = workload.Totals;
        bool drained = await WaitForDrainAsync(server).ConfigureAwait(false);
        await Task.Delay(SettleDelay).ConfigureAwait(false);

        // Taken while the server is still running: whether its sessions and groups emptied out is
        // half of what this run checks, and stopping it first would answer that question for free.
        ResourceSnapshot final = ResourceSnapshot.Capture();
        long serverErrors = server.ErrorCount - serverErrorsBefore;
        long serverMessages = server.MessagesReceived - serverMessagesBefore;

        int exitCode = Report(options, totals, baseline, final, server, wallClock.Elapsed, warmupDrained, drained, serverErrors, serverMessages);

        await server.StopAsync().ConfigureAwait(false);
        return exitCode;
    }

    private static async Task<bool> WaitForDrainAsync(SoakServer server)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < TimeSpan.FromSeconds(DrainTimeoutSeconds))
        {
            if (server.ActiveConnections is 0 && server.SessionCount is 0)
            {
                return true;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        return server.ActiveConnections is 0 && server.SessionCount is 0;
    }

    private static int Report(
        SoakOptions options,
        WorkloadTotals totals,
        ResourceSnapshot baseline,
        ResourceSnapshot final,
        SoakServer server,
        TimeSpan wallClock,
        bool warmupDrained,
        bool drained,
        long serverErrors,
        long serverMessages)
    {
        long heapGrowth = final.ManagedHeapBytes - baseline.ManagedHeapBytes;
        double heapGrowthMegabytes = heapGrowth / (double)(1024 * 1024);
        long allocated = final.TotalAllocatedBytes - baseline.TotalAllocatedBytes;
        long allocatedPerMessage = totals.Messages > 0 ? allocated / totals.Messages : 0;
        int? descriptorGrowth = final.OpenFileDescriptors - baseline.OpenFileDescriptors;

        List<string> failures = [];

        // A run that barely connected, or that spent the phase failing, would report a flat heap and
        // an empty session table for the wrong reason. Both are checked so a green result always
        // means the library was actually driven.
        if (totals.Connections < MinimumConnections)
        {
            failures.Add($"only {totals.Connections} connection(s) were opened, too few to conclude anything");
        }

        if (totals.Failures * FailureRateDivisor > totals.Connections)
        {
            failures.Add($"{totals.Failures} of {totals.Connections} connection(s) failed, above the {100 / FailureRateDivisor}% the run tolerates");
        }

        if (heapGrowthMegabytes > options.MaxHeapGrowthMegabytes)
        {
            failures.Add($"managed heap grew {heapGrowthMegabytes:F2} MB over the baseline, above the {options.MaxHeapGrowthMegabytes:F2} MB limit");
        }

        if (descriptorGrowth is int growth && growth > options.MaxFileDescriptorGrowth)
        {
            failures.Add($"{growth} file descriptors were never released, above the {options.MaxFileDescriptorGrowth} limit");
        }

        if (server.ActiveConnections is not 0)
        {
            failures.Add($"{server.ActiveConnections} connection(s) still counted as active after every client was gone");
        }

        if (server.SessionCount is not 0)
        {
            failures.Add($"{server.SessionCount} session(s) still registered after every client was gone");
        }

        if (server.GroupCount is not 0)
        {
            failures.Add($"{server.GroupCount} group(s) still hold members after every client was gone");
        }

        if (!drained)
        {
            failures.Add($"the server did not finish unwinding within {DrainTimeoutSeconds}s of the last client leaving");
        }

        Console.WriteLine();
        Console.WriteLine("StormSocket resource soak");
        Console.WriteLine("=========================");
        Console.WriteLine("run");
        WriteValue("warmup / measured", $"{options.WarmupSeconds}s / {options.Seconds}s");
        WriteValue("churn workers", options.ChurnWorkers.ToString());
        WriteValue("steady connections", options.SteadyConnections.ToString());
        WriteValue("wall clock", $"{wallClock.TotalSeconds:F1}s");
        WriteValue("warmup drained", warmupDrained ? "yes" : "no");

        Console.WriteLine("workload (measured phase)");
        WriteValue("connections opened", totals.Connections.ToString());
        WriteValue("closed by client", totals.GracefulClosures.ToString());
        WriteValue("closed by server", totals.ServerClosures.ToString());
        WriteValue("closed by TCP reset", totals.ResetClosures.ToString());
        WriteValue("messages exchanged", totals.Messages.ToString());
        WriteValue("decoded by server", serverMessages.ToString());
        WriteValue("client failures", totals.Failures.ToString());
        WriteValue("server errors raised", serverErrors.ToString());

        Console.WriteLine("memory");
        WriteValue("heap baseline", FormatMegabytes(baseline.ManagedHeapBytes));
        WriteValue("heap final", FormatMegabytes(final.ManagedHeapBytes));
        WriteValue("heap growth", $"{heapGrowthMegabytes:F2} MB (limit {options.MaxHeapGrowthMegabytes:F2} MB)");
        WriteValue("allocated during phase", FormatMegabytes(allocated));
        WriteValue("allocated per message", $"{allocatedPerMessage} B");
        WriteValue(
            "collections gen0/1/2",
            $"{final.Gen0Collections - baseline.Gen0Collections} / {final.Gen1Collections - baseline.Gen1Collections} / {final.Gen2Collections - baseline.Gen2Collections}");

        Console.WriteLine("descriptors");
        if (baseline.OpenFileDescriptors is int baselineDescriptors && final.OpenFileDescriptors is int finalDescriptors)
        {
            WriteValue("open baseline / final", $"{baselineDescriptors} / {finalDescriptors}");
            WriteValue("growth", $"{finalDescriptors - baselineDescriptors} (limit {options.MaxFileDescriptorGrowth})");
        }
        else
        {
            WriteValue("open descriptors", "not measurable on this platform");
        }

        Console.WriteLine("server state after drain");
        WriteValue("active connections", $"{server.ActiveConnections} (limit 0)");
        WriteValue("sessions", $"{server.SessionCount} (limit 0)");
        WriteValue("non-empty groups", $"{server.GroupCount} (limit 0)");
        WriteValue("connections accepted", server.TotalConnections.ToString());
        Console.WriteLine();

        if (failures.Count is 0)
        {
            Console.WriteLine("RESULT: PASS");
            return 0;
        }

        foreach (string failure in failures)
        {
            Console.WriteLine($"FAIL: {failure}");
        }

        Console.WriteLine("RESULT: FAIL");
        return 1;
    }

    private static void WriteValue(string label, string value) => Console.WriteLine($"  {label,-24}: {value}");

    private static string FormatMegabytes(long bytes) => $"{bytes / (double)(1024 * 1024):F2} MB";
}
