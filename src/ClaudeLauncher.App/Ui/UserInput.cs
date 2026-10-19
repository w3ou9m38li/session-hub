using System.Windows.Input;

namespace ClaudeLauncher.App;

/// <summary>
/// When the user last moved the pointer, clicked, scrolled or typed — in any of the program's
/// windows, not only the list's. A transcript, the usage window and the changes window are
/// windows of their own; work that stops everything for a moment (<see cref="MemoryRelief"/>)
/// has to wait for a pause in them too, or it lands while the user scrolls a long transcript.
/// </summary>
internal static class UserInput
{
    private static long _lastTicks;
    private static bool _watching;

    /// <summary>Whether there was input within the last <paramref name="milliseconds"/>.</summary>
    public static bool Within(long milliseconds) => Environment.TickCount64 - _lastTicks < milliseconds;

    /// <summary>Starts watching, once, on the thread the windows run on. Each input costs one store.</summary>
    public static void Watch()
    {
        if (_watching) return;
        _watching = true;
        InputManager.Current.PreProcessInput += (_, e) =>
        {
            if (e.StagingItem.Input is MouseEventArgs or KeyboardEventArgs) _lastTicks = Environment.TickCount64;
        };
    }
}
