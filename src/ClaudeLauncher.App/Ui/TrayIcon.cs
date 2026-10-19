using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClaudeLauncher.App;

/// <summary>
/// The launcher's icon in the notification area, while it keeps running with its window
/// closed. A click brings the window back; the right-click menu offers the same and 退出.
///
/// <para>Plain Win32 (Shell_NotifyIcon and a native popup menu) on the list window's own
/// handle: no Windows Forms for one icon, and a native menu closes properly when the user
/// clicks elsewhere even while the window is hidden. Explorer restarting drops every tray
/// icon; the icon puts itself back when that is announced.</para>
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    public sealed record Command(string Label, Action Run);

    private const int CallbackMessage = 0x8000 + 0x4C;     // WM_APP + n
    private const int NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const int NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10;
    private const int WmLButtonUp = 0x202, WmRButtonUp = 0x205, WmContextMenu = 0x7B;
    private const uint MfString = 0x0, MfSeparator = 0x800;
    private const uint TpmRightButton = 0x2, TpmReturnCmd = 0x100, TpmNoNotify = 0x80;

    private static readonly int TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    private readonly HwndSource _source;
    private readonly IntPtr _icon;
    private readonly string _tip;
    private readonly Action _open;
    private readonly IReadOnlyList<Command?> _menu;
    private bool _shown;

    /// <param name="menu">The right-click menu, top to bottom; null is a separator.</param>
    public TrayIcon(Window window, string tip, Action open, IReadOnlyList<Command?> menu)
    {
        _tip = tip.Length > 120 ? tip[..120] : tip;
        _open = open;
        _menu = menu;
        _source = HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle());
        _source.AddHook(Hook);
        ExtractIconEx(Environment.ProcessPath ?? string.Empty, 0, out var large, out _icon, 1);
        if (large != IntPtr.Zero) DestroyIcon(large);
    }

    public void Show()
    {
        if (_shown) return;
        var data = Data(NifMessage | NifIcon | NifTip);
        _shown = Shell_NotifyIcon(NimAdd, ref data);
    }

    public void Hide()
    {
        if (!_shown) return;
        var data = Data(0);
        Shell_NotifyIcon(NimDelete, ref data);
        _shown = false;
    }

    /// <summary>A notification from the icon — used once, to say where the window went.</summary>
    public void Balloon(string title, string text)
    {
        if (!_shown) return;
        var data = Data(NifInfo);
        data.szInfoTitle = title;
        data.szInfo = text;
        Shell_NotifyIcon(NimModify, ref data);
    }

    private NotifyIconData Data(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = _source.Handle,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == TaskbarCreated && _shown)
        {
            _shown = false;
            Show();
        }
        else if (msg == CallbackMessage)
        {
            switch (lParam.ToInt32() & 0xFFFF)
            {
                case WmLButtonUp:
                    _open();
                    handled = true;
                    break;
                case WmRButtonUp or WmContextMenu:
                    ShowMenu(hwnd);
                    handled = true;
                    break;
            }
        }
        return IntPtr.Zero;
    }

    private void ShowMenu(IntPtr hwnd)
    {
        var menu = CreatePopupMenu();
        try
        {
            for (var i = 0; i < _menu.Count; i++)
            {
                if (_menu[i] is { } command) AppendMenu(menu, MfString, (UIntPtr)(i + 1), command.Label);
                else AppendMenu(menu, MfSeparator, UIntPtr.Zero, null);
            }
            GetCursorPos(out var at);
            // Without being foreground first, the menu would not close when the user clicks away.
            SetForegroundWindow(hwnd);
            var chosen = TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCmd | TpmNoNotify, at.X, at.Y, hwnd, IntPtr.Zero);
            PostMessage(hwnd, 0, IntPtr.Zero, IntPtr.Zero);
            if (chosen > 0 && chosen <= _menu.Count && _menu[chosen - 1] is { } picked) picked.Run();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        Hide();
        _source.RemoveHook(Hook);
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")]
    private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr parameters);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}
