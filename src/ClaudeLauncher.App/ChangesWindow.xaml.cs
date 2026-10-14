using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using ClaudeLauncher.App.ViewModels;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Changes;
using Visibility = System.Windows.Visibility;

namespace ClaudeLauncher.App;

/// <summary>A file the conversation changed, for the list on the left.</summary>
public sealed record ChangedFile(string Path, string Name, string Folder, string Tag, int Added, int Removed,
    IReadOnlyList<FileChange> Changes)
{
    public string AddedText => $"+{Added:N0}";
    public string RemovedText => $"−{Removed:N0}";
    public string CountText => Changes.Count == 1 ? "1 次" : $"{Changes.Count} 次";
}

/// <summary>A line of the diff pane: <c>H</c> heads a change, the rest are diff lines.</summary>
public sealed record DiffRow(string Kind, string OldNo, string NewNo, string Sign, string Text, bool Failed);

/// <summary>
/// The files a conversation changed, and each change as a diff — read from the transcript, one
/// window per conversation.
/// </summary>
public partial class ChangesWindow : Window
{
    private static readonly Dictionary<ConversationKey, ChangesWindow> OpenWindows = new();

    private readonly SessionRowViewModel _row;
    private readonly string _path;
    private readonly CancellationTokenSource _closing = new();
    private List<ChangedFile> _files = [];

    public static void ShowFor(Window anchor, SessionRowViewModel row, string transcriptPath)
    {
        if (OpenWindows.TryGetValue(row.Metadata.Key, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var window = new ChangesWindow(row, transcriptPath);
        window.PlaceOver(anchor);
        OpenWindows[row.Metadata.Key] = window;
        window.Closed += (_, _) =>
        {
            OpenWindows.Remove(row.Metadata.Key);
            window._closing.Cancel();
        };
        window.Show();
    }

    private ChangesWindow(SessionRowViewModel row, string transcriptPath)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _row = row;
        _path = transcriptPath;
        Title = $"{row.Title} · 改动的文件";
        TitleLine.Text = row.Title;
        TitleLine.ToolTip = row.Title;
        Loaded += async (_, _) => await LoadAsync();
        KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) Close();
        };
    }

    private void PlaceOver(Window anchor)
    {
        var bounds = anchor.WindowState == WindowState.Normal
            ? new Rect(anchor.Left, anchor.Top, anchor.ActualWidth, anchor.ActualHeight)
            : anchor.RestoreBounds;
        var step = 28 * (OpenWindows.Count % 6);
        Left = bounds.Left + Math.Max(0, (bounds.Width - Width) / 2) + step;
        Top = bounds.Top + Math.Max(0, (bounds.Height - Height) / 2) + step;
        WindowPlacementMemory.KeepOnScreen(this);
    }

    private async Task LoadAsync()
    {
        if (_row.Source.Changes == ChangesSupport.None)
        {
            // Its records keep what was said, not an edit-by-edit record of files — or edits in a
            // form this window does not read: the source says which.
            SummaryLine.Text = _row.Source.NoChangesSummary ?? "这个来源不记录文件改动";
            EmptyText.Text = _row.Source.NoChangesText ?? "这个来源的记录里没有文件改动，这里列不出来。";
            EmptyText.Visibility = Visibility.Visible;
            return;
        }
        var codex = _row.Source.Changes == ChangesSupport.CodexPatches;
        // A copy made the copied changes in the conversation it came from, not in this one.
        var own = OwnPart.For(_row.Metadata);
        var copied = _row.IsCodex ? "分叉" : "续接";
        IReadOnlyList<FileChange> changes;
        try
        {
            var cancel = _closing.Token;
            var path = _path;
            changes = await Task.Run(() =>
            {
                // The conversation's own record, then its subagents' or spawned tasks', in time order.
                var all = new List<FileChange>(ChangeScanner.Read(path, codex, cancel, own));
                foreach (var (file, label) in ChangeScanner.Related(path, codex, codex ? CodexPaths.Discover() : null, cancel))
                {
                    try
                    {
                        all.AddRange(ChangeScanner.Read(file, codex, cancel).Select(c => c with { Tool = $"{c.Tool} · {label}" }));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // One unreadable subagent record: the rest still count.
                    }
                }
                return (IReadOnlyList<FileChange>)all.OrderBy(c => c.At ?? DateTimeOffset.MaxValue).ToList();
            }, cancel);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SummaryLine.Text = "读不了这条会话的记录文件：" + ex.Message;
            return;
        }

        var project = _row.ProjectRef is { Length: > 0 } p ? p.TrimEnd('\\', '/') : null;
        _files = changes
            .GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => Describe(g.Key, g.ToList(), project))
            .OrderBy(f => f.Folder, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (_files.Count == 0)
        {
            SummaryLine.Text = "没有找到改动";
            EmptyText.Text = (own.IsWhole
                    ? "这条会话（连同它的子代理、子任务）里没有用 Edit、Write 或 apply_patch 改过文件。"
                    : $"这条{(_row.IsCodex ? "任务" : "会话")}{copied}之后没有用 Edit、Write 或 apply_patch 改过文件；"
                      + $"{copied}前的改动记在原{(_row.IsCodex ? "任务" : "会话")}里。")
                + "\n用命令行或脚本改的文件不会记在这里。";
            EmptyText.Visibility = Visibility.Visible;
            return;
        }

        var succeeded = changes.Where(c => c.Succeeded).ToList();
        var failed = changes.Count - succeeded.Count;
        var tools = succeeded.GroupBy(c => c.Tool).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}");
        SummaryLine.Text = $"改动了 {_files.Count} 个文件　+{succeeded.Sum(c => c.Added):N0} −{succeeded.Sum(c => c.Removed):N0}"
                           + $"　·　{succeeded.Count} 次（{string.Join("、", tools)}）"
                           + (failed > 0 ? $"　·　另有 {failed} 次没有成功" : "")
                           + (changes.Count >= ChangeScanner.MaxChanges ? $"　·　只列出前 {ChangeScanner.MaxChanges} 次" : "")
                           + (own.IsWhole ? "" : $"　·　只算{copied}之后的改动，{copied}前的在原{(_row.IsCodex ? "任务" : "会话")}里");
        ShowFiles();
    }

    /// <summary>A file's line in the list: its name, the folder it is in (inside the project, relative to it), totals.</summary>
    private static ChangedFile Describe(string path, List<FileChange> changes, string? project)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        var folder = Path.GetDirectoryName(path) ?? "";
        if (project is not null && folder.StartsWith(project, StringComparison.OrdinalIgnoreCase)
            && (folder.Length == project.Length || folder[project.Length] is '\\' or '/'))
            folder = folder.Length == project.Length ? "（工作目录）" : "." + folder[project.Length..];
        var succeeded = changes.Where(c => c.Succeeded).ToList();
        var tag = changes[^1].Kind == ChangeKind.Deleted ? "　已删除"
            : changes[0].Kind == ChangeKind.Added ? "　新建"
            : succeeded.Count == 0 ? "　未成功"
            : changes[^1].MovedTo is not null ? "　已移动"
            : "";
        return new ChangedFile(path, name.Length > 0 ? name : path, folder, tag,
            succeeded.Sum(c => c.Added), succeeded.Sum(c => c.Removed), changes);
    }

    private void ShowFiles()
    {
        var filter = FilterBox.Text.Trim();
        var shown = filter.Length == 0 ? _files
            : _files.Where(f => f.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        var selected = FileList.SelectedItem as ChangedFile;
        FileList.ItemsSource = shown;
        FileList.SelectedItem = selected is not null && shown.Contains(selected) ? selected : shown.FirstOrDefault();
        if (FileList.SelectedItem is null) ShowDiff(null);
    }

    private void Filter_TextChanged(object sender, TextChangedEventArgs e)
    {
        FilterHint.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowFiles();
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ShowDiff(FileList.SelectedItem as ChangedFile);

    /// <summary>Every change to one file, oldest first, each under a heading of its own.</summary>
    private void ShowDiff(ChangedFile? file)
    {
        CopyButton.IsEnabled = RevealButton.IsEnabled = file is not null;
        PathLine.Text = file?.Path ?? "";
        PathLine.ToolTip = file?.Path;
        if (file is null)
        {
            DiffList.ItemsSource = null;
            return;
        }
        var rows = new List<DiffRow>();
        for (var i = 0; i < file.Changes.Count; i++)
        {
            var change = file.Changes[i];
            rows.Add(new DiffRow("H", "", "", "", Heading(change, i + 1, file.Changes.Count), !change.Succeeded));
            foreach (var line in change.Lines)
            {
                var kind = line.Kind.ToString();
                rows.Add(new DiffRow(kind,
                    line.Old?.ToString(CultureInfo.InvariantCulture) ?? "",
                    line.New?.ToString(CultureInfo.InvariantCulture) ?? "",
                    line.Kind is '+' or '-' ? kind : "",
                    line.Text.Replace("\t", "    ", StringComparison.Ordinal),
                    !change.Succeeded));
            }
        }
        DiffList.ItemsSource = rows;
        if (rows.Count > 0) DiffList.ScrollIntoView(rows[0]);
    }

    private static string Heading(FileChange change, int number, int of)
    {
        var what = change.Kind switch
        {
            ChangeKind.Added => "新建",
            ChangeKind.Deleted => "删除",
            _ => change.MovedTo is not null ? "移动并修改" : "修改",
        };
        var when = change.At is { } at ? at.ToLocalTime().ToString("M月d日 HH:mm", CultureInfo.InvariantCulture) : "时间未记录";
        return $"{(of > 1 ? $"第 {number} 次　·　" : "")}{when}　·　{change.Tool}　·　{what}　+{change.Added} −{change.Removed}"
               + (change.MovedTo is { } to ? $"　→ {to}" : "")
               + (change.Succeeded ? "" : "　·　没有成功（工具报错）");
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not ChangedFile file) return;
        var text = new StringBuilder();
        foreach (var change in file.Changes)
        {
            text.Append("--- ").AppendLine(change.Kind == ChangeKind.Added ? "/dev/null" : file.Path);
            text.Append("+++ ").AppendLine(change.Kind == ChangeKind.Deleted ? "/dev/null" : change.MovedTo ?? file.Path);
            foreach (var line in change.Lines)
            {
                switch (line.Kind)
                {
                    case '@': text.AppendLine(line.Text.StartsWith("@@", StringComparison.Ordinal) ? line.Text : "@@ " + line.Text); break;
                    case '…': text.Append("# ").AppendLine(line.Text); break;
                    default: text.Append(line.Kind).AppendLine(line.Text); break;
                }
            }
        }
        try
        {
            Clipboard.SetText(text.ToString());
            SummaryLine.Text = $"已复制 {file.Name} 的 {file.Changes.Count} 次改动";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            SummaryLine.Text = "剪贴板被别的程序占用，没能复制";
        }
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not ChangedFile file) return;
        if (!File.Exists(file.Path))
        {
            SummaryLine.Text = "这个文件已经不在原处了（可能被移动、删除，或在另一台电脑上）";
            return;
        }
        Shell.Reveal(file.Path);
    }
}
