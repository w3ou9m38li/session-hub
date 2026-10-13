using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ClaudeLauncher.Infrastructure.Settings;

namespace ClaudeLauncher.App;

/// <summary>
/// Asks Windows for a title bar in the theme's colours. Without it every window of this tool
/// opens in the dark theme as a dark page under a white strip — the first thing the eye lands on.
/// </summary>
internal static class DarkTitleBar
{
    private const int ImmersiveDarkMode = 20;          // Windows 10 20H1 and later
    private const int ImmersiveDarkModeLegacy = 19;    // earlier Windows 10 builds
    private const int CaptionColor = 35;               // Windows 11 only; ignored elsewhere

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window) =>
        window.SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                var on = Theme.IsLight ? 0 : 1;
                if (DwmSetWindowAttribute(hwnd, ImmersiveDarkMode, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, ImmersiveDarkModeLegacy, ref on, sizeof(int));
                // COLORREF is 0x00BBGGRR: the window background.
                var color = Theme.WindowColor;
                var caption = color.R | (color.G << 8) | (color.B << 16);
                DwmSetWindowAttribute(hwnd, CaptionColor, ref caption, sizeof(int));
            }
            catch (DllNotFoundException)
            {
                // No DWM (an unusual session type): the light title bar is only cosmetic.
            }
        };
}

/// <summary>
/// Puts a window back where the user left it — but only onto a screen that still exists.
/// A monitor unplugged since last time must not leave the window open somewhere invisible.
/// </summary>
internal static class WindowPlacementMemory
{
    private const uint MonitorDefaultToNull = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref Rect rect, uint flags);

    public static void Restore(Window window, WindowPlacement? placement, bool restorePosition = true)
    {
        if (placement is not { IsPlausible: true }) return;

        // A size saved on a larger monitor than any attached now is cut down to what exists.
        window.Width = Math.Max(window.MinWidth, Math.Min(placement.Width, SystemParameters.VirtualScreenWidth));
        window.Height = Math.Max(window.MinHeight, Math.Min(placement.Height, SystemParameters.VirtualScreenHeight));
        if (!restorePosition) return;

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = placement.Left;
        window.Top = placement.Top;

        // Checked against the real window rectangle once it exists, which sidesteps every
        // DPI conversion between the saved units and the monitor that now shows them.
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (GetWindowRect(hwnd, out var r))
            {
                var titleBar = new Rect { Left = r.Left + 40, Top = r.Top, Right = r.Right - 40, Bottom = r.Top + 32 };
                if (MonitorFromRect(ref titleBar, MonitorDefaultToNull) == IntPtr.Zero)
                {
                    var area = SystemParameters.WorkArea;
                    window.Left = area.Left + Math.Max(0, (area.Width - window.Width) / 2);
                    window.Top = area.Top + Math.Max(0, (area.Height - window.Height) / 2);
                    return;
                }
            }
            if (placement.Maximized) window.WindowState = WindowState.Maximized;
        };
    }

    /// <summary>After the window exists: if its title bar is on no monitor, centre it on the main screen.</summary>
    public static void KeepOnScreen(Window window) =>
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (!GetWindowRect(hwnd, out var r)) return;
            var titleBar = new Rect { Left = r.Left + 40, Top = r.Top, Right = r.Right - 40, Bottom = r.Top + 32 };
            if (MonitorFromRect(ref titleBar, MonitorDefaultToNull) != IntPtr.Zero) return;
            var area = SystemParameters.WorkArea;
            window.Left = area.Left + Math.Max(0, (area.Width - window.Width) / 2);
            window.Top = area.Top + Math.Max(0, (area.Height - window.Height) / 2);
        };

    public static WindowPlacement Capture(Window window)
    {
        var bounds = window.WindowState == WindowState.Normal
            ? new System.Windows.Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)
            : window.RestoreBounds;
        return new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            window.WindowState == WindowState.Maximized);
    }
}
