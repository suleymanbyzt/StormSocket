namespace StormSocket.Benchmark.Soak;

/// <summary>
/// A point-in-time reading of the process resources a networking leak shows up in.
/// </summary>
internal readonly record struct ResourceSnapshot(
    long ManagedHeapBytes,
    long TotalAllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    int? OpenFileDescriptors)
{
    /// <summary>
    /// Forces a full collection and reads the resulting heap size and collection counters.
    /// </summary>
    /// <remarks>
    /// Collected repeatedly because one pass only queues objects with finalizers — sockets and
    /// streams among them — for finalization; their memory is not released until a later collection.
    /// </remarks>
    public static ResourceSnapshot Capture()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        return new ResourceSnapshot(
            GC.GetTotalMemory(forceFullCollection: true),
            GC.GetTotalAllocatedBytes(precise: false),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            TryCountOpenFileDescriptors());
    }

    /// <summary>
    /// Counts the descriptors the process holds open, or null where that cannot be read portably.
    /// </summary>
    /// <remarks>
    /// Only Linux exposes this as a directory listing. macOS has no /proc and Windows counts handles
    /// of every kind, which is far too noisy to threshold, so both are reported as unavailable rather
    /// than guessed at. CI runs on Linux, which is where the check has to hold.
    /// </remarks>
    private static int? TryCountOpenFileDescriptors()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            // The enumeration itself holds a descriptor while it runs, so every sample is off by the
            // same one and the difference between two samples stays exact.
            return Directory.EnumerateFileSystemEntries("/proc/self/fd").Count();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
