using System.Runtime.InteropServices;

namespace ClaudeLauncher.Infrastructure.Diagnostics;

/// <summary>
/// Windows' background processing mode: lowest CPU priority and, more to the point, very low
/// disk and memory priority. A pass that reads or copies gigabytes of transcripts then never
/// makes the window wait for the disk — opening a conversation, paging the list — however much
/// it has left to do.
/// </summary>
public static class LowPriority
{
    private const int ThreadModeBackgroundBegin = 0x00010000;
    private const int ThreadModeBackgroundEnd = 0x00020000;
    private const uint ProcessModeBackgroundBegin = 0x00100000;
    private const uint ProcessModeBackgroundEnd = 0x00200000;

    /// <summary>Enters the mode on this thread; false when Windows declined (the work then runs at its normal priority).</summary>
    public static bool EnterThread() => SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin);

    public static void LeaveThread() => SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundEnd);

    /// <summary>
    /// Enters the mode for the whole process — every thread, those a library starts of its own
    /// included. Only for a process that does nothing but background work; false when Windows declined.
    /// </summary>
    public static bool EnterProcess() => SetPriorityClass(GetCurrentProcess(), ProcessModeBackgroundBegin);

    public static void LeaveProcess() => SetPriorityClass(GetCurrentProcess(), ProcessModeBackgroundEnd);

    /// <summary>Long work on a thread of its own below normal priority, so it never competes with the window.</summary>
    public static Task<T> Run<T>(Func<T> work, string name = "background pass")
    {
        ArgumentNullException.ThrowIfNull(work);
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            // Gigabytes read or copied without the window ever waiting for the disk behind them.
            var background = EnterThread();
            try { done.SetResult(work()); }
            catch (OperationCanceledException) { done.SetCanceled(); }
            catch (Exception ex) { done.SetException(ex); }
            finally
            {
                if (background) LeaveThread();
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = name,
        };
        thread.Start();
        return done.Task;
    }

    /// <inheritdoc cref="Run{T}(Func{T}, string)"/>
    public static Task Run(Action work, string name = "background pass")
    {
        ArgumentNullException.ThrowIfNull(work);
        return Run(() =>
        {
            work();
            return true;
        }, name);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(IntPtr thread, int priority);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPriorityClass(IntPtr process, uint priorityClass);
}
