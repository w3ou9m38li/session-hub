using System.Runtime.InteropServices;

namespace ClaudeLauncher.App;

/// <summary>
/// Windows' background processing mode for the calling thread: lowest CPU priority and, more
/// to the point, very low disk and memory priority. A pass that reads or copies gigabytes of
/// transcripts then never makes the window wait for the disk — opening a conversation, paging
/// the list — however much it has left to do.
/// </summary>
internal static class BackgroundMode
{
    private const int ThreadModeBackgroundBegin = 0x00010000;
    private const int ThreadModeBackgroundEnd = 0x00020000;

    /// <summary>Enters the mode on this thread; false when Windows declined (the pass then runs at its normal priority).</summary>
    public static bool Enter() => SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin);

    public static void Leave() => SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundEnd);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(IntPtr thread, int priority);
}
