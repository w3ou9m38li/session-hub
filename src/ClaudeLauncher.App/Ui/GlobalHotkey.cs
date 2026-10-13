using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace ClaudeLauncher.App;

/// <summary>
/// A key combination that reaches this window from anywhere in Windows. Registered with the
/// system for the window's handle; another program already holding the combination simply
/// means it is not registered, which <see cref="Registered"/> says.
/// </summary>
internal sealed class GlobalHotkey : IDisposable
{
    public const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;
    public const uint VkSpace = 0x20;

    /// <summary>A combination to try, and how to name it.</summary>
    public sealed record Combination(string Name, uint Modifiers, uint Key);

    /// <summary>
    /// Tried in turn until one is free: another program — an input method, a screenshot tool —
    /// may already hold the first, and a hotkey that silently does nothing is worse than a
    /// different one that works.
    /// </summary>
    public static readonly Combination[] Candidates =
    [
        new("Ctrl+Alt+Space", ModControl | ModAlt, VkSpace),
        new("Alt+Shift+Space", ModAlt | ModShift, VkSpace),
        new("Ctrl+Alt+K", ModControl | ModAlt, 0x4B),
    ];

    /// <summary>
    /// The first free combination, registered — <paramref name="preferred"/> (the user's own
    /// choice) before the usual ones; null when every one is taken.
    /// </summary>
    public static GlobalHotkey? RegisterFirstFree(Window window, Action pressed, Combination? preferred = null)
    {
        foreach (var combination in preferred is null ? Candidates : [preferred, .. Candidates])
        {
            var hotkey = new GlobalHotkey(window, combination.Modifiers, combination.Key, pressed) { Name = combination.Name };
            if (hotkey.Registered) return hotkey;
            hotkey.Dispose();
        }
        return null;
    }

    public string Name { get; private init; } = string.Empty;

    /// <summary>The combination for these modifiers and key, named the way menus name shortcuts.</summary>
    public static Combination From(ModifierKeys modifiers, Key key)
    {
        uint mods = 0;
        if (modifiers.HasFlag(ModifierKeys.Control)) mods |= ModControl;
        if (modifiers.HasFlag(ModifierKeys.Alt)) mods |= ModAlt;
        if (modifiers.HasFlag(ModifierKeys.Shift)) mods |= ModShift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) mods |= ModWin;
        return new Combination(Describe(modifiers, key), mods, (uint)KeyInterop.VirtualKeyFromKey(key));
    }

    /// <summary>"Ctrl+Alt+J": modifiers in the usual order, then the key; without a key, the modifiers so far.</summary>
    public static string Describe(ModifierKeys modifiers, Key? key)
    {
        var parts = new List<string>(5);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (key is { } k) parts.Add(KeyName(k));
        return string.Join("+", parts);
    }

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (key - Key.NumPad0),
        Key.Space => "Space",
        Key.OemTilde => "`",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.OemPipe => "\\",
        _ => key.ToString(),
    };

    /// <summary>
    /// Whether modifiers make a combination fit to be global: two of them, one being Ctrl, Alt
    /// or Win. With one — Ctrl+C, Alt+F, Win+E — it would take a shortcut every program already
    /// uses away from all of them.
    /// </summary>
    public static bool IsAcceptable(ModifierKeys modifiers)
    {
        var count = 0;
        foreach (var modifier in new[] { ModifierKeys.Control, ModifierKeys.Alt, ModifierKeys.Shift, ModifierKeys.Windows })
            if (modifiers.HasFlag(modifier)) count++;
        return count >= 2 && (modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0;
    }

    /// <summary>A combination saved by name, back to what the system takes; null for a name that is not one — or not a fit one.</summary>
    public static Combination? Parse(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var parts = name.Split('+');
        var modifiers = ModifierKeys.None;
        foreach (var part in parts[..^1])
        {
            modifiers |= part switch
            {
                "Ctrl" => ModifierKeys.Control,
                "Alt" => ModifierKeys.Alt,
                "Shift" => ModifierKeys.Shift,
                "Win" => ModifierKeys.Windows,
                _ => ModifierKeys.None,
            };
        }
        if (!IsAcceptable(modifiers)) return null;          // a hand-edited "Shift+A" is not taken
        foreach (var key in Enum.GetValues<Key>())
            if (key != Key.None && KeyName(key) == parts[^1]) return From(modifiers, key);
        return null;
    }

    /// <summary>Whether a combination could be registered now: tried on the window, let go at once.</summary>
    public static bool IsFree(IntPtr hwnd, Combination combination)
    {
        const int Probe = 0x4C44;
        if (!RegisterHotKey(hwnd, Probe, combination.Modifiers | ModNoRepeat, combination.Key)) return false;
        UnregisterHotKey(hwnd, Probe);
        return true;
    }

    private const int WmHotkey = 0x0312;
    private const int Id = 0x4C43;       // any id unique within this window

    private readonly HwndSource _source;
    private readonly Action _pressed;

    public bool Registered { get; }

    public GlobalHotkey(Window window, uint modifiers, uint key, Action pressed)
    {
        _pressed = pressed;
        var handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(handle);
        _source.AddHook(Hook);
        Registered = RegisterHotKey(handle, Id, modifiers | ModNoRepeat, key);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == Id)
        {
            handled = true;
            _pressed();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (Registered) UnregisterHotKey(_source.Handle, Id);
        _source.RemoveHook(Hook);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
