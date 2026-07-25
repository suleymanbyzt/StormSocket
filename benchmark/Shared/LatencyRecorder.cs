using System.Diagnostics;

namespace StormSocket.Benchmark;

/// <summary>
/// Collects per-message round-trip times and reports them as percentiles.
/// </summary>
/// <remarks>
/// Throughput and latency are different measurements and cannot be derived from one another: a
/// pipelined run can push millions of messages per second while any individual message waits
/// milliseconds behind the ones queued in front of it. Dividing elapsed time by message count
/// reports the former while labelling it the latter, which is why round-trips are timed
/// individually here, at pipeline depth 1.
/// </remarks>
public sealed class LatencyRecorder
{
    private readonly long[] _samples;
    private int _count;

    public LatencyRecorder(int capacity)
    {
        _samples = new long[capacity];
    }

    public int Count => Volatile.Read(ref _count);

    /// <summary>Records one round-trip, measured in <see cref="Stopwatch"/> ticks.</summary>
    public void Record(long elapsedTicks)
    {
        int index = Interlocked.Increment(ref _count) - 1;
        if (index < _samples.Length)
        {
            _samples[index] = elapsedTicks;
        }
    }

    public void Report(string title)
    {
        int count = Math.Min(Volatile.Read(ref _count), _samples.Length);
        if (count == 0)
        {
            Console.WriteLine($"{title}: no samples");
            return;
        }

        long[] sorted = _samples[..count];
        Array.Sort(sorted);

        double total = 0;
        foreach (long sample in sorted)
        {
            total += sample;
        }

        Console.WriteLine();
        Console.WriteLine($"{title} ({count:N0} round-trips, pipeline depth 1)");
        Console.WriteLine($"  mean   {Format(total / count)}");
        Console.WriteLine($"  min    {Format(sorted[0])}");
        Console.WriteLine($"  p50    {Format(Percentile(sorted, 0.50))}");
        Console.WriteLine($"  p90    {Format(Percentile(sorted, 0.90))}");
        Console.WriteLine($"  p99    {Format(Percentile(sorted, 0.99))}");
        Console.WriteLine($"  p99.9  {Format(Percentile(sorted, 0.999))}");
        Console.WriteLine($"  max    {Format(sorted[^1])}");
    }

    private static double Percentile(long[] sorted, double percentile)
    {
        int index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private static string Format(double ticks)
    {
        double microseconds = ticks * 1_000_000.0 / Stopwatch.Frequency;
        return microseconds < 1000
            ? $"{microseconds,9:N2} us"
            : $"{microseconds / 1000,9:N2} ms";
    }
}
