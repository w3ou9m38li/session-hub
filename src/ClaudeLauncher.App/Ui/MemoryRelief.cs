namespace ClaudeLauncher.App;

/// <summary>
/// Hands memory back to the system after work that went through a lot of it: the first pass
/// over every transcript after an update, the usage window's first count, a very long
/// transcript read and closed again. The garbage collector frees such memory but keeps it
/// for later, and a program that is idle collects nothing more — so the few hundred megabytes
/// a long pass went through stayed with the program for as long as it ran. A collection that
/// gives it back stops everything for a moment; it is done only when that much has gone
/// through since the last one, and only while the user has paused.
/// </summary>
internal static class MemoryRelief
{
    /// <summary>What has to go through memory before handing some back is worth the pause.</summary>
    private const long Worth = 256L * 1024 * 1024;

    /// <summary>
    /// And what the collector has to be holding: in everyday use it holds about twenty
    /// megabytes, and the once-a-minute refresh goes through memory without adding to that.
    /// </summary>
    private const long Held = 64L * 1024 * 1024;

    /// <summary>What had gone through memory the last time; counted from the start of the program before that.</summary>
    private static long _allocatedAtLast;
    private static bool _running;

    /// <summary>Whether enough has gone through memory since the last time, and enough of it is still held.</summary>
    public static bool IsDue =>
        !_running
        && GC.GetTotalAllocatedBytes() - _allocatedAtLast >= Worth
        && GC.GetGCMemoryInfo().TotalCommittedBytes >= Held;

    /// <summary>Collects everything and gives the free memory back; nothing when it is not due.</summary>
    public static async Task ReleaseAsync()
    {
        if (!IsDue) return;
        _running = true;
        try
        {
            await Task.Run(() => GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true));
        }
        finally
        {
            _allocatedAtLast = GC.GetTotalAllocatedBytes();
            _running = false;
        }
    }
}
