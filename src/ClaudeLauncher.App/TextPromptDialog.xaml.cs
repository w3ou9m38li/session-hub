using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ClaudeLauncher.App;

/// <summary>
/// Asks for one piece of text — a conversation's own name, a note — with an optional way to
/// remove what is there. A multi-line box keeps Enter for new lines and saves on Ctrl+Enter.
/// </summary>
public partial class TextPromptDialog : Window
{
    public enum Outcome { Cancelled, Saved, Cleared }

    private readonly bool _multiline;
    private Outcome _outcome = Outcome.Cancelled;

    private TextPromptDialog(bool multiline)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _multiline = multiline;
        if (multiline)
        {
            InputBox.AcceptsReturn = true;
            InputBox.TextWrapping = TextWrapping.Wrap;
            InputBox.Height = 120;
            InputBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            InputBox.VerticalContentAlignment = VerticalAlignment.Top;
        }
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    /// <param name="clearLabel">Offers removing the current value ("恢复原标题", "删除备注"); null when there is none.</param>
    public static (Outcome Result, string Text) Ask(
        Window owner, string title, string prompt, string initial, string hint,
        string? clearLabel = null, bool multiline = false, int maxLength = 120)
    {
        var dialog = new TextPromptDialog(multiline)
        {
            Owner = owner,
            Title = title,
        };
        dialog.PromptLine.Text = prompt;
        dialog.InputBox.Text = initial;
        dialog.InputBox.MaxLength = maxLength;
        dialog.HintLine.Text = multiline ? hint + "　Ctrl+Enter 保存" : hint;
        if (clearLabel is not null)
        {
            dialog.ClearButton.Content = clearLabel;
            dialog.ClearButton.Visibility = Visibility.Visible;
        }
        dialog.ShowDialog();
        return (dialog._outcome, dialog.InputBox.Text);
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // In a multi-line box Enter is a new line; Ctrl+Enter saves, as in most editors.
        if (_multiline && e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Close(Outcome.Saved);
            e.Handled = true;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close(Outcome.Saved);

    private void Clear_Click(object sender, RoutedEventArgs e) => Close(Outcome.Cleared);

    private void Close(Outcome outcome)
    {
        _outcome = outcome;
        DialogResult = true;
    }
}
