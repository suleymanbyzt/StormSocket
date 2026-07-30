using System.Globalization;

namespace StormSocket.Benchmark.Soak;

/// <summary>
/// Command-line configuration for a soak run, including the limits that decide pass or fail.
/// </summary>
internal sealed class SoakOptions
{
    /// <summary>Duration of the measured phase.</summary>
    public int Seconds { get; private set; } = 90;

    /// <summary>
    /// Duration of the unmeasured phase that runs first.
    /// </summary>
    /// <remarks>
    /// The baseline is taken after this phase, so everything that grows once and then stays —
    /// JIT-compiled code, the array pools, the pipe segment pools, the thread pool's threads — is
    /// already paid for and does not show up as a leak.
    /// </remarks>
    public int WarmupSeconds { get; private set; } = 20;

    /// <summary>Number of workers that connect, exchange a little traffic and disconnect in a loop.</summary>
    public int ChurnWorkers { get; private set; } = 8;

    /// <summary>Number of long-lived connections carrying steady mixed text/binary traffic.</summary>
    public int SteadyConnections { get; private set; } = 16;

    /// <summary>Managed heap growth over the baseline that fails the run.</summary>
    /// <remarks>
    /// A default run opens roughly 18,000 connections and exchanges 150,000 messages after the
    /// baseline is taken, and the heap moves by well under a megabyte in either direction across
    /// that — the limit sits an order of magnitude above the noise so a busier or slower runner does
    /// not turn it red on its own. What it is sized to catch is per-connection state that is never
    /// released: at these connection counts a retained buffer or transport is tens of megabytes.
    /// Retained bookkeeping too small to show up here — a session or a group entry — is caught by the
    /// active-connection, session and group checks instead, which have to come back to exactly zero.
    /// </remarks>
    public double MaxHeapGrowthMegabytes { get; private set; } = 12.0;

    /// <summary>Growth in open file descriptors over the baseline that fails the run (Linux only).</summary>
    /// <remarks>
    /// A socket that is never closed leaks one descriptor per connection, so a real leak reaches
    /// thousands within the run. The allowance covers descriptors that legitimately appear between
    /// the two samples — an epoll registration or a lazily opened runtime file.
    /// </remarks>
    public int MaxFileDescriptorGrowth { get; private set; } = 32;

    /// <summary>Usage text printed when the arguments cannot be parsed.</summary>
    public static string Usage =>
        """
        Usage: StormSocket.Benchmark.Soak [options]

          -s, --seconds <n>            Measured phase duration in seconds (default 90)
          -w, --warmup <n>             Warmup phase duration in seconds (default 20)
              --churn <n>              Connect/disconnect workers (default 8)
              --steady <n>             Long-lived connections (default 16)
              --max-heap-growth-mb <n> Managed heap growth allowed over baseline (default 12)
              --max-fd-growth <n>      Open descriptor growth allowed over baseline (default 32)
          -h, --help                   Print this text
        """;

    /// <summary>Parses command-line arguments, reporting the first problem instead of throwing.</summary>
    public static bool TryParse(string[] args, out SoakOptions options, out string? error)
    {
        options = new SoakOptions();
        error = null;

        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];
            switch (argument)
            {
                case "-h" or "--help":
                    error = string.Empty;
                    return false;

                case "-s" or "--seconds":
                    if (!TryReadPositiveInt(args, ref i, out int seconds, out error))
                    {
                        return false;
                    }

                    options.Seconds = seconds;
                    break;

                case "-w" or "--warmup":
                    if (!TryReadPositiveInt(args, ref i, out int warmup, out error))
                    {
                        return false;
                    }

                    options.WarmupSeconds = warmup;
                    break;

                case "--churn":
                    if (!TryReadPositiveInt(args, ref i, out int churn, out error))
                    {
                        return false;
                    }

                    options.ChurnWorkers = churn;
                    break;

                case "--steady":
                    if (!TryReadPositiveInt(args, ref i, out int steady, out error))
                    {
                        return false;
                    }

                    options.SteadyConnections = steady;
                    break;

                case "--max-heap-growth-mb":
                    if (!TryReadPositiveDouble(args, ref i, out double heapGrowth, out error))
                    {
                        return false;
                    }

                    options.MaxHeapGrowthMegabytes = heapGrowth;
                    break;

                case "--max-fd-growth":
                    if (!TryReadPositiveInt(args, ref i, out int fdGrowth, out error))
                    {
                        return false;
                    }

                    options.MaxFileDescriptorGrowth = fdGrowth;
                    break;

                default:
                    error = $"Unknown argument '{argument}'.";
                    return false;
            }
        }

        return true;
    }

    private static bool TryReadPositiveInt(string[] args, ref int index, out int value, out string? error)
    {
        if (!TryReadValue(args, ref index, out string? raw, out error))
        {
            value = 0;
            return false;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value <= 0)
        {
            error = $"Value for '{args[index - 1]}' must be a positive integer, but was '{raw}'.";
            return false;
        }

        return true;
    }

    private static bool TryReadPositiveDouble(string[] args, ref int index, out double value, out string? error)
    {
        if (!TryReadValue(args, ref index, out string? raw, out error))
        {
            value = 0;
            return false;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || value <= 0)
        {
            error = $"Value for '{args[index - 1]}' must be a positive number, but was '{raw}'.";
            return false;
        }

        return true;
    }

    private static bool TryReadValue(string[] args, ref int index, out string? value, out string? error)
    {
        if (index + 1 >= args.Length)
        {
            value = null;
            error = $"Argument '{args[index]}' requires a value.";
            return false;
        }

        value = args[++index];
        error = null;
        return true;
    }
}
