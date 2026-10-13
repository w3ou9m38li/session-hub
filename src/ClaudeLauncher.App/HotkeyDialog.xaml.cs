using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace ClaudeLauncher.App;

/// <summary>
/// Records the combination the user presses for the global hotkey and says at once whether it
/// can be had — another program may already hold it. Esc on its own cancels and Enter on its
/// own saves, since neither can be a hotkey without modifiers; Alt+F4 closes the dialog as it
/// closes any window.
/// </summary>
public partial class HotkeyDialog : Window
{
    public enum Outcome { Cancelled, Saved, Automatic }

    private GlobalHotkey.Combination? _pressed;
    private Outcome _outcome = Outcome.Cancelled;

    private HotkeyDialog(string? active, bool automatic)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        ComboText.Text = active ?? "（无）";
        Say(false, active is null ? "现在没有可用的全局快捷键。"
            : automatic ? "这是自动选出的；按下新的组合即可换成你自己的。"
            : "这是你设的；按下新的组合即可替换。");
        PreviewKeyDown += OnKey;
    }

    /// <param name="active">The combination in use, by name; the caller lets go of it while the dialog is open.</param>
    /// <param name="automatic">Whether it was picked automatically rather than chosen.</param>
    /// <returns>The outcome and, when saved, the combination's name.</returns>
    public static (Outcome Result, string? Name) Ask(Window owner, string? active, bool automatic)
    {
        var dialog = new HotkeyDialog(active, automatic) { Owner = owner };
        dialog.ShowDialog();
        return (dialog._outcome, dialog._pressed?.Name);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) modifiers |= ModifierKeys.Windows;
        if (modifiers == ModifierKeys.Alt && key == Key.F4) return;        // closes the dialog, as anywhere
        e.Handled = true;
        if (modifiers == ModifierKeys.None && key == Key.Escape)
        {
            Close(Outcome.Cancelled);
            return;
        }
        if (modifiers == ModifierKeys.None && key == Key.Enter)
        {
            if (SaveButton.IsEnabled) Close(Outcome.Saved);
            return;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed)
        {
            ComboText.Text = GlobalHotkey.Describe(modifiers, null) + "…";
            return;
        }

        var combination = GlobalHotkey.From(modifiers, key);
        ComboText.Text = combination.Name;
        if (!GlobalHotkey.IsAcceptable(modifiers))
        {
            _pressed = null;
            SaveButton.IsEnabled = false;
            Say(true, "要两个修饰键，其中有 Ctrl、Alt 或 Win（如 Ctrl+Alt、Alt+Shift、Ctrl+Win）：只带一个的组合"
                      + "（Ctrl+C 这类）会把各个程序里常用的快捷键都抢走。");
            return;
        }

        _pressed = combination;
        var free = GlobalHotkey.IsFree(new WindowInteropHelper(this).Handle, combination);
        SaveButton.IsEnabled = free;
        Say(!free, free ? "可以用。" : "已被其他程序占用，换一个组合试试。");
    }

    private void Say(bool warning, string text)
    {
        StateText.Text = text;
        StateGlyph.Text = warning ? "\uE7BA" : "\uE946";
        StateText.Foreground = (System.Windows.Media.Brush)FindResource(warning ? "WarnBrush" : "MutedBrush");
        StateGlyph.Foreground = StateText.Foreground;
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Close(Outcome.Saved);

    private void Automatic_Click(object sender, RoutedEventArgs e) => Close(Outcome.Automatic);

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close(Outcome.Cancelled);

    private void Close(Outcome outcome)
    {
        _outcome = outcome;
        DialogResult = outcome != Outcome.Cancelled;
    }
}
