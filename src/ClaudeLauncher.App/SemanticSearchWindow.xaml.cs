using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClaudeLauncher.App.ViewModels;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Semantic;
using Visibility = System.Windows.Visibility;

namespace ClaudeLauncher.App;

/// <summary>A conversation found by meaning, as a line of the results.</summary>
/// <param name="Find">What the transcript is searched for when opened: the start of the closest piece.</param>
public sealed record SemanticResultItem(
    SessionMetadata Row, string Title, string App, Brush AppBrush, string Time, string Who, string Snippet, string Tip,
    GridLength Bar, GridLength Rest, string ScoreTip, string? Find);

/// <summary>
/// Search by meaning: a sentence in, the conversations closest to it out — each with the piece of
/// it that came closest. One window; the worker that answers is started by the first search and
/// ends by itself a few minutes after the last. Closing the window lets go of its results.
/// </summary>
public partial class SemanticSearchWindow : Window
{
    private static SemanticSearchWindow? _open;

    private readonly MainViewModel _vm;
    private readonly Action<SessionMetadata> _locate;
    private readonly Action<SessionMetadata, string?, Window> _openTranscript;
    private CancellationTokenSource? _searching;
    private SemanticOverview? _overview;
    private Func<Task>? _emptyAction;
    private bool _searched;

    /// <param name="locate">Shows a conversation in the list.</param>
    /// <param name="openTranscript">Opens a conversation's transcript over the given window, looking for the given words.</param>
    public static void ShowFor(Window anchor, MainViewModel vm, Action<SessionMetadata> locate,
        Action<SessionMetadata, string?, Window> openTranscript)
    {
        if (_open is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            existing.QueryBox.Focus();
            existing.QueryBox.SelectAll();
            return;
        }
        var window = new SemanticSearchWindow(vm, locate, openTranscript);
        window.PlaceOver(anchor);
        _open = window;
        window.Closed += (_, _) =>
        {
            _open = null;
            window._searching?.Cancel();
            window._vm.PropertyChanged -= window.OnViewModelChanged;
            // Its results go with it.
            window.Results.ItemsSource = null;
        };
        window.Show();
    }

    /// <summary>Closes the window if it is open: search by meaning was switched off.</summary>
    public static void CloseIfOpen() => _open?.Close();

    private SemanticSearchWindow(MainViewModel vm, Action<SessionMetadata> locate, Action<SessionMetadata, string?, Window> openTranscript)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _vm = vm;
        _locate = locate;
        _openTranscript = openTranscript;
        _vm.PropertyChanged += OnViewModelChanged;
        Loaded += async (_, _) =>
        {
            QueryBox.Focus();
            await RefreshOverviewAsync(open: true);
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    /// <summary>Centred on the list's window.</summary>
    private void PlaceOver(Window anchor)
    {
        var bounds = anchor.WindowState == WindowState.Normal
            ? new Rect(anchor.Left, anchor.Top, anchor.ActualWidth, anchor.ActualHeight)
            : anchor.RestoreBounds;
        Left = bounds.Left + Math.Max(0, (bounds.Width - Width) / 2);
        Top = bounds.Top + Math.Max(0, (bounds.Height - Height) / 2);
        WindowPlacementMemory.KeepOnScreen(this);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // A build began, ended or was paused: what the index holds is read again.
            case nameof(MainViewModel.SemanticState):
                _ = RefreshOverviewAsync(open: false);
                break;
            // Its progress, several times a second: only the count on the line changes.
            case nameof(MainViewModel.SemanticProgress) when _overview is { } overview:
                var (done, total) = _vm.SemanticBuildProgress;
                _overview = overview with { State = _vm.SemanticState, Done = done, Total = total };
                StatusText.Text = Describe(_overview);
                break;
        }
    }

    // ---------------------------------------------------------------- status

    private int _overviewVersion;

    private async Task RefreshOverviewAsync(bool open)
    {
        var version = ++_overviewVersion;
        SemanticOverview overview;
        try
        {
            overview = open ? await _vm.OpenSemanticSearchAsync() : await _vm.DescribeSemanticAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
        {
            if (version == _overviewVersion) StatusText.Text = "读不了语义索引：" + ex.Message;
            return;
        }
        if (version != _overviewVersion || !IsLoaded) return;
        _overview = overview;
        StatusText.Text = Describe(overview);
        if (!_searched) ShowStart(overview);
    }

    /// <summary>「语义索引：N 段对话、M 个片段，最近更新 …」, and how the build goes.</summary>
    private static string Describe(SemanticOverview o)
    {
        var parts = new List<string>();
        parts.Add(o.Stats is { Chunks: > 0 } stats
            ? $"语义索引：{stats.Conversations} 段对话、{stats.Chunks} 个片段"
              + (stats.Updated is { } updated ? $"，最近更新 {updated.ToLocalTime():M月d日 HH:mm}" : "")
            : "语义索引还是空的");
        switch (o.State)
        {
            case SemanticBuildState.Building:
                parts.Add(o.Total > 0 ? $"正在建立 {o.Done}/{o.Total}" : "正在建立");
                break;
            case SemanticBuildState.Paused:
                parts.Add("已暂停建立（「⋯」菜单里可以继续）");
                break;
        }
        if (!o.ContentSearch) parts.Add("对话内容搜索已关闭，索引不再更新");
        return string.Join("　·　", parts);
    }

    /// <summary>Before the first search: what to type, or what is missing first.</summary>
    private void ShowStart(SemanticOverview o)
    {
        if (!o.Readiness.Ready)
        {
            ShowEmpty("还不能按意思搜索", string.Join("\n", o.Readiness.Problems)
                + "\n\n会话管家不会自动下载任何文件。放好后再搜一次就行，不用重开。", "打开模型文件夹", OpenModelFolderAsync);
            return;
        }
        if (o.Stats is not { Chunks: > 0 })
        {
            var why = o.ContentSearch
                ? o.State == SemanticBuildState.Building
                    ? "正在后台建立（进度见上面一行），建好一部分就能搜到那一部分。"
                    : o.Problem is { } problem ? "上次建立没有完成：" + problem : "下次同步后会在后台开始建立。"
                : "语义索引是从对话内容索引建出来的：先在主窗口「⋯」菜单里打开「搜索对话内容」。";
            ShowEmpty("语义索引还没有内容", why, null, null);
            return;
        }
        ShowEmpty("输入一句话，按 Enter", "按意思找意思相近的对话：不必记得原话，换个说法也能找到。", null, null);
    }

    private void ShowEmpty(string title, string detail, string? actionLabel, Func<Task>? action)
    {
        Results.ItemsSource = null;
        Results.Visibility = Visibility.Collapsed;
        EmptyPanel.Visibility = Visibility.Visible;
        EmptyTitle.Text = title;
        EmptyDetail.Text = detail;
        _emptyAction = action;
        EmptyAction.Content = actionLabel;
        EmptyAction.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void EmptyAction_Click(object sender, RoutedEventArgs e)
    {
        if (_emptyAction is { } action) await action();
    }

    private Task OpenModelFolderAsync()
    {
        var folder = MainViewModel.SemanticModelFolder;
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText.Text = "建不了模型文件夹：" + ex.Message;
            return Task.CompletedTask;
        }
        if (Shell.Open(folder) is { } error) StatusText.Text = "打开失败：" + error;
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- search

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e) =>
        QueryHint.Visibility = QueryBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void QueryBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                _ = SearchAsync();
                e.Handled = true;
                break;
            // From the box straight into the results.
            case Key.Down when Results.Items.Count > 0:
                if (Results.SelectedIndex < 0) Results.SelectedIndex = 0;
                (Results.ItemContainerGenerator.ContainerFromIndex(Results.SelectedIndex) as ListBoxItem)?.Focus();
                e.Handled = true;
                break;
        }
    }

    private void Search_Click(object sender, RoutedEventArgs e) => _ = SearchAsync();

    private async Task SearchAsync()
    {
        var text = QueryBox.Text.Trim();
        if (text.Length == 0)
        {
            QueryBox.Focus();
            return;
        }
        _searching?.Cancel();
        var cts = new CancellationTokenSource();
        _searching = cts;
        _searched = true;
        SearchButton.IsEnabled = false;
        StatusText.Text = _vm.SemanticWorkerRunning ? "正在找…" : "正在载入向量模型并查找…（第一次要几秒）";
        try
        {
            var found = await _vm.SearchSemanticAsync(text, cts.Token);
            if (cts.IsCancellationRequested) return;
            ShowResults(found);
            await RefreshOverviewAsync(open: false);
        }
        catch (OperationCanceledException)
        {
            // Closed, or a newer search took over.
        }
        catch (SemanticUnavailableException ex)
        {
            if (cts.IsCancellationRequested) return;
            ShowEmpty("现在搜不了", ex.Message, _overview?.Readiness.Ready == false ? "打开模型文件夹" : null,
                _overview?.Readiness.Ready == false ? OpenModelFolderAsync : null);
            await RefreshOverviewAsync(open: false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException
                                       or Microsoft.Data.Sqlite.SqliteException)
        {
            if (!cts.IsCancellationRequested) ShowEmpty("现在搜不了", ex.Message, null, null);
        }
        finally
        {
            if (ReferenceEquals(_searching, cts)) SearchButton.IsEnabled = true;
        }
    }

    private void ShowResults(IReadOnlyList<SemanticResult> found)
    {
        if (found.Count == 0)
        {
            ShowEmpty("没有找到意思相近的对话",
                _overview is { Stats.Chunks: > 0 } ? "换个说法试试，或者说得具体一点。" : "语义索引还是空的：等它在后台建好一部分再来搜。",
                null, null);
            return;
        }
        var best = Math.Max(found.Max(f => f.Hit.Score), 1e-6f);
        Results.ItemsSource = found.Select(f => Item(f, best)).ToList();
        EmptyPanel.Visibility = Visibility.Collapsed;
        Results.Visibility = Visibility.Visible;
        Results.ScrollIntoView(Results.Items[0]);
        Results.SelectedIndex = -1;
    }

    private static SemanticResultItem Item(SemanticResult result, float best)
    {
        var row = new SessionRowViewModel(result.Row);
        var closest = result.Hit.Chunks[0];
        var share = Math.Clamp(result.Hit.Score / best, 0.04, 1);
        var tip = new StringBuilder(row.Title);
        foreach (var chunk in result.Hit.Chunks)
        {
            tip.Append("\n\n").Append(WhoSaid(chunk.Role));
            if (chunk.Stamp is { } stamp) tip.Append('（').Append(stamp).Append('）');
            tip.Append(chunk.Snippet);
        }
        return new SemanticResultItem(result.Row, row.Title, row.Backend, row.BackendBrush, row.LastActivity,
            WhoSaid(closest.Role), closest.Snippet, tip.ToString(),
            new GridLength(share, GridUnitType.Star), new GridLength(1 - share, GridUnitType.Star),
            $"接近程度 {result.Hit.Score.ToString("0.00", CultureInfo.InvariantCulture)}（越接近 1 越像）",
            FindTerm(closest.Snippet));
    }

    private static string WhoSaid(string role) => role == "user" ? "你：" : "助手：";

    /// <summary>
    /// What the transcript is searched for: the piece's first words as written — its snippet only
    /// joins lines, so up to the first space it is the message's own text.
    /// </summary>
    private static string? FindTerm(string snippet)
    {
        var first = snippet.TrimEnd('…').Split(' ', 2)[0];
        if (first.Length > 24) first = first[..(char.IsHighSurrogate(first[23]) ? 23 : 24)];
        return first.Length >= 2 ? first : null;
    }

    // --------------------------------------------------------------- results

    private void Results_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as DependencyObject) is { } source
            && ItemsControl.ContainerFromElement(Results, source) is ListBoxItem { DataContext: SemanticResultItem item })
            _locate(item.Row);
    }

    private void Results_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Results.SelectedItem is not SemanticResultItem item) return;
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _openTranscript(item.Row, item.Find, this);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            _locate(item.Row);
            e.Handled = true;
        }
    }
}
