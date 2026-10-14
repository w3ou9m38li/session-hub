using System.IO;
using System.Windows;
using ClaudeLauncher.Infrastructure.Backends;
using Microsoft.Win32;

namespace ClaudeLauncher.App;

/// <summary>
/// Imports Gemini's web history: how to export it from Google Takeout, step by step, then the
/// export to read. Also where an import is removed again. The dialog only chooses; the caller
/// imports or removes.
/// </summary>
public partial class TakeoutImportDialog : Window
{
    public enum Outcome { Closed, Import, Remove }

    private Outcome _outcome = Outcome.Closed;
    private IReadOnlyList<string> _files = [];
    private FoundExport? _found;

    private TakeoutImportDialog()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
    }

    public static (Outcome Result, IReadOnlyList<string> Files) Ask(Window owner, GeminiTakeoutStore store)
    {
        var dialog = new TakeoutImportDialog { Owner = owner };
        var (conversations, prompts, last) = Describe(store);
        dialog.Loaded += async (_, _) => await dialog.FindAsync(last);
        if (conversations > 0)
        {
            dialog.StatusLine.Text = $"已导入 {conversations} 段对话、{prompts} 条提问"
                                     + (last is { } at ? $"，上次导入在 {at:yyyy-MM-dd HH:mm}" : "")
                                     + "。再导入一份新的导出，只会补上新增的部分。";
            dialog.RemoveButton.Visibility = Visibility.Visible;
        }
        else
        {
            dialog.StatusLine.Text = "还没有导入过。";
        }
        dialog.ShowDialog();
        return (dialog._outcome, dialog._files);
    }

    private static (int, int, DateTime?) Describe(GeminiTakeoutStore store)
    {
        try
        {
            return store.Describe();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, 0, null);
        }
    }

    /// <summary>Offers the newest Google Takeout with Gemini's activity in the Downloads folder, when it is newer than the last import.</summary>
    private async Task FindAsync(DateTime? lastImport)
    {
        var found = await Task.Run(() => ExportFinder.Newest(ExportFinder.Kind.GeminiTakeout, ExportFinder.DownloadsFolder, lastImport));
        if (found is null || !IsLoaded) return;
        _found = found;
        FoundText.Text = $"下载文件夹里有一份{(lastImport is null ? "" : "新的")} Google Takeout 导出：{Path.GetFileName(found.Path)}"
                         + $"（{found.Saved:MM-dd HH:mm} 下载，{found.Bytes / (1024.0 * 1024):0.#}\u00A0MB）";
        FoundPanel.Visibility = Visibility.Visible;
    }

    private void ImportFound_Click(object sender, RoutedEventArgs e)
    {
        if (_found is null) return;
        _files = [_found.Path];
        _outcome = Outcome.Import;
        DialogResult = true;
    }

    private void OpenTakeout_Click(object sender, RoutedEventArgs e)
    {
        if (Shell.Open("https://takeout.google.com/") is { } error)
            MessageDialog.Show(this, DialogKind.Warning, "打不开浏览器", error + "\n\n可以自己在浏览器里打开 takeout.google.com。");
    }

    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var picker = new OpenFileDialog
        {
            Title = "选择 Google Takeout 的导出",
            Filter = "Google Takeout 导出（*.zip; *.json; *.html）|*.zip;*.json;*.html|所有文件|*.*",
            Multiselect = true,
            CheckFileExists = true,
            InitialDirectory = Directory.Exists(downloads) ? downloads : null,
        };
        if (picker.ShowDialog(this) != true || picker.FileNames.Length == 0) return;
        _files = picker.FileNames;
        _outcome = Outcome.Import;
        DialogResult = true;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var choice = MessageDialog.Show(this, DialogKind.Question, "移除已导入的 Gemini 网页记录？",
            "删掉会话管家自己保存的这份副本：列表里不再有这些对话，它们的置顶、备注和分组也一并去掉。\n\n"
            + "Google 那边的记录、你下载的导出文件都不受影响，以后可以再导入。",
            primary: "移除", cancel: "取消");
        if (choice != DialogChoice.Primary) return;
        _outcome = Outcome.Remove;
        DialogResult = true;
    }
}
