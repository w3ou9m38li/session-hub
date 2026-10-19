using System.Windows;
using System.Windows.Input;

namespace ClaudeLauncher.App;

public enum DialogKind { Info, Warning, Error, Question }

public enum DialogChoice { Primary, Secondary, Cancel }

/// <summary>
/// The tool's own message box, in its own colours. The system one is a white panel with a
/// system sound dropped into a dark window — jarring for something as ordinary as "这条会话
/// 没有正文". Esc always means "cancel / close", whatever the buttons are called.
/// </summary>
public partial class MessageDialog : Window
{
    private DialogChoice _choice = DialogChoice.Cancel;

    private MessageDialog()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            _choice = DialogChoice.Cancel;
            e.Handled = true;
            Close();
        };
    }

    public static DialogChoice Show(
        Window? owner, DialogKind kind, string heading, string body,
        string primary = "知道了", string? secondary = null, string? cancel = null)
    {
        var dialog = new MessageDialog();
        if (owner is { IsLoaded: true }) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        (dialog.Glyph.Text, dialog.Glyph.Foreground) = kind switch
        {
            DialogKind.Warning => ("", (System.Windows.Media.Brush)dialog.FindResource("WarnBrush")),
            DialogKind.Error => ("", (System.Windows.Media.Brush)dialog.FindResource("ErrorBrush")),
            DialogKind.Question => ("", (System.Windows.Media.Brush)dialog.FindResource("InfoBrush")),
            _ => ("", (System.Windows.Media.Brush)dialog.FindResource("InfoBrush")),
        };
        dialog.Heading.Text = heading;
        dialog.Body.Text = body;
        dialog.Body.Visibility = string.IsNullOrWhiteSpace(body) ? Visibility.Collapsed : Visibility.Visible;

        dialog.PrimaryButton.Content = primary;
        if (secondary is not null)
        {
            dialog.SecondaryButton.Content = secondary;
            dialog.SecondaryButton.Visibility = Visibility.Visible;
        }
        if (cancel is not null)
        {
            dialog.CancelButton.Content = cancel;
            dialog.CancelButton.Visibility = Visibility.Visible;
        }

        // Enter takes the primary answer. Nothing this tool asks through a dialog is
        // irreversible — organising actions can be undone — so that default is safe.
        dialog.Loaded += (_, _) => dialog.PrimaryButton.Focus();
        dialog.ShowDialog();
        return dialog._choice;
    }

    private void Primary_Click(object sender, RoutedEventArgs e) => Finish(DialogChoice.Primary);
    private void Secondary_Click(object sender, RoutedEventArgs e) => Finish(DialogChoice.Secondary);
    private void Cancel_Click(object sender, RoutedEventArgs e) => Finish(DialogChoice.Cancel);

    private void Finish(DialogChoice choice)
    {
        _choice = choice;
        Close();
    }
}
