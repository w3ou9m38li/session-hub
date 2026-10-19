using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ClaudeLauncher.App.ViewModels;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Settings;
using Microsoft.Win32;
using Visibility = System.Windows.Visibility;

namespace ClaudeLauncher.App;

/// <summary>
/// Imports ChatGPT's or Claude's conversations on the web: how to export them from the app's
/// own settings, step by step, then the export to read. Also where an import is removed again.
/// The dialog only chooses; the caller imports or removes.
/// </summary>
public partial class WebExportImportDialog : Window
{
    public enum Outcome { Closed, Import, Remove }

    private sealed record Kind(string App, string Site, string SiteLabel, string[] Steps, string Note);

    private static readonly Kind ChatGpt = new(
        "ChatGPT", "https://chatgpt.com/#settings/DataControls", "打开 chatgpt.com 的设置",
        [
            "在 chatgpt.com 或 ChatGPT 桌面版点左下角的头像 → **设置**（Settings）→ **数据管理**（Data controls）。",
            "在「导出数据」一行点 **导出**（Export），再点 **确认导出**（Confirm export）。",
            "ChatGPT 会发一封邮件（几分钟到几小时），点里面的 **下载数据导出**（Download data export）；链接 24 小时内有效。",
            "下载的压缩包不用解压，回到这里点「选择导出文件…」。已经解压了的，选里面的 conversations.json 也行。",
        ],
        "导入的是每段对话你最后看到的那一版（改过的提问留下的旧分支不列），连同你发的和 ChatGPT 生成的图片；导出里没有 token 数，用量统计不含网页版。");

    private static readonly Kind Claude = new(
        "Claude", "https://claude.ai/settings", "打开 claude.ai 的设置",
        [
            "在 claude.ai 或 Claude 桌面版点左下角的头像 → **设置**（Settings）→ **隐私**（Privacy）。",
            "点 **导出数据**（Export data）。",
            "Claude 会发一封邮件，点里面的下载链接；链接 24 小时内有效。",
            "下载的压缩包不用解压，回到这里点「选择导出文件…」。已经解压了的，选里面的 conversations.json 也行。",
        ],
        "Claude 的导出里只有文字：你发的图片和文件只写名字，从文档里取出的文字一并导入；导出里没有 token 数，用量统计不含网页版。");

    private readonly Kind _kind;
    private Outcome _outcome = Outcome.Closed;
    private IReadOnlyList<string> _files = [];
    private FoundExport? _found;

    private WebExportImportDialog(string backend)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _kind = backend == ConversationKey.ClaudeWeb ? Claude : ChatGpt;
        var accent = Palette.TextOf(Sources.Describe(backend).Family);

        Title = $"导入 {_kind.App} 网页版的对话";
        Heading.Text = $"把 {_kind.App} 网页版的对话导进来";
        Intro.Text = $"网页版（以及桌面版里的普通聊天）的记录存在 {_kind.App} 那边：先在它的设置里导出一份，再在这里选中下载的压缩包。"
                     + "会话管家只读这份导出，复制到自己的数据目录里，之后能在列表里搜索、查看、导出；"
                     + "以后再导入新的导出，新的对话会补上、变过的会更新，网页上删掉的对话这里照样留着。";
        StepsHeading.Text = $"在 {_kind.App} 里导出";
        OpenSiteButton.Content = _kind.SiteLabel;
        OpenSiteButton.ToolTip = $"在浏览器里打开 {_kind.App} 的设置（需要登录你的账号）";
        RemoveButton.ToolTip = $"删掉会话管家自己保存的这份副本；{_kind.App} 那边的记录不受影响";
        Note.Text = _kind.Note;

        for (var i = 0; i < _kind.Steps.Length; i++)
        {
            Steps.RowDefinitions.Add(new RowDefinition());
            var last = i == _kind.Steps.Length - 1;
            var number = new TextBlock
            {
                Text = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Foreground = accent,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, last ? 0 : 8),
            };
            Grid.SetRow(number, i);
            Steps.Children.Add(number);
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(0, 0, 0, last ? 0 : 8) };
            Rich(text, _kind.Steps[i]);
            Grid.SetRow(text, i);
            Grid.SetColumn(text, 1);
            Steps.Children.Add(text);
        }
    }

    public static (Outcome Result, IReadOnlyList<string> Files) Ask(Window owner, WebExportStore store)
    {
        var dialog = new WebExportImportDialog(store.Backend) { Owner = owner };
        var kind = store.Backend == ConversationKey.ClaudeWeb ? ExportKinds.Claude : ExportKinds.ChatGpt;
        dialog.StatusLine.Text = "正在查看已导入的记录…";
        dialog.Loaded += async (_, _) => await dialog.LoadImportedAsync(store, kind);
        dialog.ShowDialog();
        return (dialog._outcome, dialog._files);
    }

    /// <summary>
    /// What is imported already, read off the UI thread — thousands of conversations to list —
    /// then the newest export in Downloads, offered only when it is newer than the last import.
    /// </summary>
    private async Task LoadImportedAsync(WebExportStore store, ExportKinds kind)
    {
        var imported = await Task.Run(() => Describe(store));
        if (!IsLoaded) return;
        ShowImported(imported);
        await FindAsync(kind, imported.LastImport);
    }

    private void ShowImported((bool Exists, int Conversations, DateTime? LastImport) imported)
    {
        var (exists, conversations, last) = imported;
        StatusLine.Text = conversations > 0
            ? $"已导入 {conversations} 段对话"
              + (last is { } at ? $"，上次导入在 {at:yyyy-MM-dd HH:mm}" : "")
              + "。再导入一份新的导出，只会补上新增的、更新变过的。"
            : exists ? "已导入的记录里没有对话。" : "还没有导入过。";
        if (exists) RemoveButton.Visibility = Visibility.Visible;
    }

    private static (bool Exists, int Conversations, DateTime? LastImport) Describe(WebExportStore store)
    {
        var exists = store.Exists;
        try
        {
            var (conversations, last) = store.Describe();
            return (exists, conversations, last);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (exists, 0, null);
        }
    }

    /// <summary>
    /// Offers the newest export of this app in the Downloads folder, when it is newer than the last
    /// import and not imported already (or found empty); once the import is removed, the one imported before.
    /// </summary>
    private async Task FindAsync(ExportKinds kind, DateTime? lastImport)
    {
        var handled = AppSettings.Current.HandledExports;
        var found = await Task.Run(() => HandledExport.Suggest(handled, kind, ExportFinder.DownloadsFolder, lastImport));
        if (found is null || !IsLoaded) return;
        _found = found;
        FoundText.Text = $"下载文件夹里有一份{(lastImport is null ? "" : "新的")} {_kind.App} 数据导出：{Path.GetFileName(found.Path)}"
                         + $"（{found.SavedUtc.ToLocalTime():MM-dd HH:mm} 下载，{found.Bytes / (1024.0 * 1024):0.#}\u00A0MB）";
        FoundPanel.Visibility = Visibility.Visible;
    }

    private void ImportFound_Click(object sender, RoutedEventArgs e)
    {
        if (_found is null) return;
        _files = [_found.Path];
        _outcome = Outcome.Import;
        DialogResult = true;
    }

    /// <summary>A step's words, with the names to look for on screen — written **like this** — in bold.</summary>
    private static void Rich(TextBlock block, string text)
    {
        var parts = text.Split("**");
        for (var i = 0; i < parts.Length; i++)
            if (parts[i].Length > 0)
                block.Inlines.Add(i % 2 == 1 ? new Bold(new Run(parts[i])) : new Run(parts[i]));
    }

    private void OpenSite_Click(object sender, RoutedEventArgs e)
    {
        if (Shell.Open(_kind.Site) is { } error)
            MessageDialog.Show(this, DialogKind.Warning, "打不开浏览器", error + $"\n\n可以自己在浏览器里打开 {_kind.Site}。");
    }

    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        var downloads = ExportFinder.DownloadsFolder;
        var picker = new OpenFileDialog
        {
            Title = $"选择 {_kind.App} 的数据导出",
            Filter = $"{_kind.App} 的数据导出（*.zip; conversations.json）|*.zip;*.json|所有文件|*.*",
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
        var choice = MessageDialog.Show(this, DialogKind.Question, $"移除已导入的 {_kind.App} 网页记录？",
            "删掉会话管家自己保存的这份副本：列表里不再有这些对话，它们的置顶、备注和分组也一并去掉。\n\n"
            + $"{_kind.App} 那边的记录、你下载的导出文件都不受影响，以后可以再导入。",
            primary: "移除", cancel: "取消");
        if (choice != DialogChoice.Primary) return;
        _outcome = Outcome.Remove;
        DialogResult = true;
    }
}
