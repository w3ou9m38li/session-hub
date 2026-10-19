using System.Windows.Threading;
using ClaudeLauncher.Infrastructure;
using ClaudeLauncher.Infrastructure.Settings;

namespace ClaudeLauncher.App;

/// <summary>
/// The settings of this run, saved a moment after the last change rather than on every one:
/// dragging a column edge produces dozens of width changes and none of them is worth a write.
/// </summary>
internal static class AppSettings
{
    private static LauncherSettings? _current;
    private static DispatcherTimer? _saveTimer;

    /// <summary>
    /// Changed since the last save. A run that only read the settings (a jump-list entry) writes
    /// nothing: a settings file of defaults in a data root nobody prepared would mark it in use.
    /// </summary>
    private static bool _changed;

    /// <summary>Loaded on first use — after the data root has been prepared, which is why it is not a static initialiser.</summary>
    public static LauncherSettings Current => _current ??= SettingsStore.Load(LauncherPaths.SettingsFile);

    public static void Update(Func<LauncherSettings, LauncherSettings> change)
    {
        _current = change(Current);
        _changed = true;
        _saveTimer ??= CreateTimer();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Writes now. Called on exit, when a pending delayed save would never run.</summary>
    public static void Flush()
    {
        _saveTimer?.Stop();
        Save();
    }

    private static void Save()
    {
        if (!_changed || _current is null) return;
        SettingsStore.Save(LauncherPaths.SettingsFile, _current);
        _changed = false;
    }

    private static DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Save();
        };
        return timer;
    }
}
