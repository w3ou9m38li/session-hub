using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClaudeLauncher.App.ViewModels;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Core.Display;
using ClaudeLauncher.Infrastructure.Search;
using ClaudeLauncher.Infrastructure.Usage;
using Visibility = System.Windows.Visibility;

namespace ClaudeLauncher.App;

/// <summary>A day's (or a month's) bar: heights in pixels, every app to one scale.</summary>
public sealed record UsageBar(string Label, string Tip, double ClaudeHeight, double CodexHeight, double GeminiHeight);

/// <summary>A ranked line: a model, a folder or a conversation, and its share of the largest.</summary>
public sealed record UsageRank(string Name, string Note, string Amount, string Tip, GridLength Bar, GridLength Rest,
    Brush Brush, SessionMetadata? Row)
{
    /// <summary>A conversation can be shown in the list; nothing else can.</summary>
    public Cursor? Cursor => Row is null ? null : Cursors.Hand;
}

/// <summary>
/// How many tokens Claude, Codex and Gemini CLI used on this computer — by day, model, folder and
/// conversation — read from the transcripts on demand. One window; opening it again brings it
/// forward and reads what is new.
/// </summary>
public partial class UsageWindow : Window
{
    private static UsageWindow? _open;

    private readonly MainViewModel _vm;
    private readonly Action<SessionMetadata> _locate;
    private MainViewModel.UsageReport? _report;
    private CancellationTokenSource? _loading;
    private int _days;
    private UsageApp? _app;
    private bool _ready;

    private const double ChartHeight = 112;

    /// <param name="locate">Shows a conversation in the list.</param>
    public static void ShowFor(Window anchor, MainViewModel vm, Action<SessionMetadata> locate)
    {
        if (_open is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            _ = existing.LoadAsync();
            return;
        }
        var window = new UsageWindow(vm, locate);
        if (AppSettings.Current.UsageWindow is null) window.PlaceOver(anchor);
        _open = window;
        window.Closed += (_, _) =>
        {
            _open = null;
            window._loading?.Cancel();
        };
        window.Show();
    }

    private UsageWindow(MainViewModel vm, Action<SessionMetadata> locate)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        WindowPlacementMemory.Restore(this, AppSettings.Current.UsageWindow);
        _vm = vm;
        _locate = locate;

        var settings = AppSettings.Current;
        _days = settings.UsageDays;
        _app = AppOf(settings.UsageApp);
        (_days switch { 1 => RangeToday, 7 => RangeWeek, 90 => RangeQuarter, 0 => RangeAll, _ => RangeMonth }).IsChecked = true;
        (_app switch { UsageApp.Claude => AppClaude, UsageApp.Codex => AppCodex, UsageApp.Gemini => AppGemini, _ => AppBoth }).IsChecked = true;
        _ready = true;

        Closing += (_, _) => AppSettings.Update(s => s with { UsageWindow = WindowPlacementMemory.Capture(this) });
        Loaded += async (_, _) => await LoadAsync();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.F5) _ = LoadAsync();
        };
    }

    /// <summary>Centred on the list's window the first time; afterwards where it was left.</summary>
    private void PlaceOver(Window anchor)
    {
        var bounds = anchor.WindowState == WindowState.Normal
            ? new Rect(anchor.Left, anchor.Top, anchor.ActualWidth, anchor.ActualHeight)
            : anchor.RestoreBounds;
        Left = bounds.Left + Math.Max(0, (bounds.Width - Width) / 2);
        Top = bounds.Top + Math.Max(0, (bounds.Height - Height) / 2);
        WindowPlacementMemory.KeepOnScreen(this);
    }

    // ------------------------------------------------------------------ load

    private async Task LoadAsync()
    {
        _loading?.Cancel();
        var cts = new CancellationTokenSource();
        _loading = cts;
        RefreshButton.IsEnabled = false;
        StatusText.Text = "正在读取对话记录里的用量…";
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            if (!cts.IsCancellationRequested && p.Total >= 5 && p.Done < p.Total)
                StatusText.Text = $"正在读取对话记录里的用量 {p.Done}/{p.Total}"
                                  + (_report is null ? "（第一次要读完全部记录，约半分钟）" : "");
        });
        try
        {
            var report = await _vm.ReadUsageAsync(progress, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (report is null)
            {
                StatusText.Text = "打不开用量数据库：数据目录可能不可写";
                return;
            }
            _report = report;
            Render();
            StatusText.Text = $"读自 {report.Files} 个记录文件　·　{DateTime.Now:HH:mm} 更新";
        }
        catch (OperationCanceledException)
        {
            // Closed, or a newer load took over.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException
                                       or Microsoft.Data.Sqlite.SqliteException)
        {
            if (!cts.IsCancellationRequested) StatusText.Text = "读取用量失败：" + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_loading, cts)) RefreshButton.IsEnabled = true;
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

    private void Range_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton { Tag: string tag }) return;
        _days = int.Parse(tag, CultureInfo.InvariantCulture);
        AppSettings.Update(s => s with { UsageDays = _days });
        Render();
    }

    private void App_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton { Tag: string tag }) return;
        _app = AppOf(tag);
        AppSettings.Update(s => s with { UsageApp = _app?.ToString() });
        Render();
    }

    private static UsageApp? AppOf(string? name) => name switch
    {
        "Claude" => UsageApp.Claude,
        "Codex" => UsageApp.Codex,
        "Gemini" => UsageApp.Gemini,
        _ => null,
    };

    private static string AppLabel(UsageApp app) => app switch
    {
        UsageApp.Codex => "Codex",
        UsageApp.Gemini => "Gemini",
        _ => "Claude",
    };

    private static Brush AppBrush(UsageApp app) => app switch
    {
        UsageApp.Codex => Palette.CodexText,
        UsageApp.Gemini => Palette.GeminiText,
        _ => Palette.ClaudeText,
    };

    private void Rank_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: UsageRank { Row: { } row } })
        {
            e.Handled = true;
            _locate(row);
        }
    }

    // ---------------------------------------------------------------- render

    private void Render()
    {
        if (_report is not { } report) return;
        var today = DateOnly.FromDateTime(DateTime.Today);
        DateOnly? since = _days > 0 ? today.AddDays(1 - _days) : null;
        var usage = report.Usage.Where(u => (since is null || u.Day >= since) && (_app is null || u.App == _app)).ToList();
        var rows = new Dictionary<string, SessionMetadata>(StringComparer.Ordinal);
        foreach (var row in report.Rows) rows.TryAdd(ContentIndex.KeyOf(row.Key), row);

        var total = Sum(usage);
        TotalValue.Text = Tokens(total.Total);
        TotalNote.Text = $"{total.Calls:N0} 次调用";
        InputValue.Text = Tokens(total.Input);
        CacheReadValue.Text = Tokens(total.CacheRead);
        CacheReadNote.Text = total.Total > 0 ? $"占合计 {(double)total.CacheRead / total.Total:P0}" : "每次调用重读的上下文";
        CacheWriteValue.Text = Tokens(total.CacheWrite);
        OutputValue.Text = Tokens(total.Output);

        RenderChart(usage, since, today);

        // By model and app: 「（未记录模型）」 of Claude and of Codex are two lines.
        ModelList.ItemsSource = Rank(usage.GroupBy(u => $"{u.App}\u0001{u.Model}"), 20, g => (
            g.First().Model == "unknown" ? "（未记录模型）" : g.First().Model,
            "　" + AppLabel(g.First().App),
            g.First().Model == "unknown" ? "记录里没有写明是哪个模型" : g.First().Model,
            AppBrush(g.First().App),
            null));

        // One folder whatever the case of its letters; two folders of one name tell their parents apart.
        const string Unlisted = "\u0001";
        var projects = usage.GroupBy(u => rows.TryGetValue(u.Conversation, out var r) ? r.ProjectRef ?? "" : Unlisted,
            StringComparer.OrdinalIgnoreCase).ToList();
        var sameName = projects.Where(g => g.Key is { Length: > 0 } and not Unlisted)
            .GroupBy(g => PathDisplay.FolderName(g.Key) ?? g.Key, StringComparer.OrdinalIgnoreCase)
            .Where(names => names.Count() > 1).Select(names => names.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ProjectList.ItemsSource = Rank(projects, 30, g =>
        {
            if (g.Key == Unlisted)
                return ("（列表里没有的会话）", "", "Codex 子任务等不在列表里的记录", Palette.Faint, null);
            if (g.Key.Length == 0)
                return ("（没有记录工作目录）", "", "这些会话没有记录工作目录", Palette.Info, null);
            var name = PathDisplay.FolderName(g.Key) ?? g.Key;
            var parent = sameName.Contains(name) ? Path.GetDirectoryName(g.Key.TrimEnd('\\', '/')) : null;
            return (name, parent is null ? "" : "　" + parent, g.Key, Palette.Info, null);
        });

        ConversationList.ItemsSource = Rank(usage.GroupBy(u => u.Conversation), 60, g =>
        {
            var app = g.First().App;
            if (rows.TryGetValue(g.Key, out var row))
            {
                var title = string.IsNullOrWhiteSpace(row.LocalTitle) ? row.Title : row.DisplayTitle;
                var folder = PathDisplay.FolderName(row.ProjectRef);
                return (title, "　" + AppLabel(app) + (folder is null ? "" : " · " + folder),
                    title + (row.ProjectRef is null ? "" : "\n" + row.ProjectRef), AppBrush(app),
                    row);
            }
            var id = g.Key[(g.Key.LastIndexOf('|') + 1)..];
            return ($"（列表里没有：{(id.Length > 8 ? id[..8] : id)}）", "　" + AppLabel(app),
                "这条记录的会话不在列表里：可能是子任务，或应用里已删除。\n" + id, Palette.Faint, null);
        });

        EmptyText.Visibility = total.Calls == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Days of the range as bars — months for 全部 — each bar split between the two apps.</summary>
    private void RenderChart(List<UsageRow> usage, DateOnly? since, DateOnly today)
    {
        // A single day is the cards above; a chart of one bar says nothing more.
        if (_days == 1)
        {
            ChartSection.Visibility = Visibility.Collapsed;
            return;
        }
        ChartSection.Visibility = Visibility.Visible;

        var byMonth = since is null;
        var first = since ?? (usage.Count > 0 ? usage.Min(u => u.Day) : today);
        if (first > today) first = today;               // a clock set back: the chart still has today
        var buckets = new List<(DateOnly From, string Label, string Name)>();
        if (byMonth)
        {
            for (var m = new DateOnly(first.Year, first.Month, 1); m <= today; m = m.AddMonths(1))
                buckets.Add((m, $"{m.Month}月", $"{m.Year} 年 {m.Month} 月"));
        }
        else
        {
            for (var d = first; d <= today; d = d.AddDays(1))
                buckets.Add((d, $"{d.Month}/{d.Day}", d.ToString("M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN"))));
        }

        var sums = buckets.Select(b =>
        {
            var inBucket = usage.Where(u => byMonth
                ? u.Day.Year == b.From.Year && u.Day.Month == b.From.Month
                : u.Day == b.From).ToList();
            return (Claude: Sum(inBucket.Where(u => u.App == UsageApp.Claude)),
                    Codex: Sum(inBucket.Where(u => u.App == UsageApp.Codex)),
                    Gemini: Sum(inBucket.Where(u => u.App == UsageApp.Gemini)));
        }).ToList();
        var peak = Math.Max(1, sums.Count == 0 ? 1 : sums.Max(s => s.Claude.Total + s.Codex.Total + s.Gemini.Total));
        var step = buckets.Count <= 12 ? 1 : buckets.Count <= 31 ? 5 : buckets.Count <= 92 ? 15 : (int)Math.Ceiling(buckets.Count / 8.0);

        Chart.ItemsSource = buckets.Select((b, i) =>
        {
            var (claude, codex, gemini) = sums[i];
            var all = claude + codex + gemini;
            var apps = new[] { ("Claude", claude), ("Codex", codex), ("Gemini", gemini) }.Where(a => a.Item2.Calls > 0).ToList();
            var tip = $"{b.Name}\n合计 {Tokens(all.Total)}　·　{all.Calls:N0} 次调用"
                      + (apps.Count > 1 ? "\n" + string.Join("　·　", apps.Select(a => $"{a.Item1} {Tokens(a.Item2.Total)}")) : "")
                      + (all.Calls > 0 ? $"\n输入 {Tokens(all.Input)}　缓存读取 {Tokens(all.CacheRead)}　缓存写入 {Tokens(all.CacheWrite)}　输出 {Tokens(all.Output)}" : "");
            return new UsageBar((buckets.Count - 1 - i) % step == 0 ? b.Label : "", tip,
                ChartHeight * claude.Total / peak, ChartHeight * codex.Total / peak, ChartHeight * gemini.Total / peak);
        }).ToList();

        ChartTitle.Text = byMonth ? "按月" : "按天";
        var top = sums.Select((s, i) => (Total: s.Claude.Total + s.Codex.Total + s.Gemini.Total, i)).MaxBy(x => x.Total);
        ChartPeak.Text = top.Total > 0 ? $"最多的一{(byMonth ? "个月" : "天")}：{buckets[top.i].Name}，{Tokens(top.Total)}" : "";
    }

    /// <summary>The largest groups first, each with its share of the first.</summary>
    private static List<UsageRank> Rank(IEnumerable<IGrouping<string, UsageRow>> groups, int take,
        Func<IGrouping<string, UsageRow>, (string Name, string Note, string Tip, Brush Brush, SessionMetadata? Row)> describe)
    {
        var ranked = groups.Select(g => (Group: g, Tokens: Sum(g))).OrderByDescending(x => x.Tokens.Total).Take(take).ToList();
        var largest = Math.Max(1, ranked.Count == 0 ? 1 : ranked[0].Tokens.Total);
        return ranked.Select(x =>
        {
            var (name, note, tip, brush, row) = describe(x.Group);
            var share = (double)x.Tokens.Total / largest;
            return new UsageRank(name, note, Tokens(x.Tokens.Total),
                tip + "\n" + Breakdown(x.Tokens) + (row is null ? "" : "\n双击在列表中定位"),
                new GridLength(share, GridUnitType.Star), new GridLength(1 - share, GridUnitType.Star), brush, row);
        }).ToList();
    }

    private static string Breakdown(TokenCounts t) =>
        $"合计 {Tokens(t.Total)}　·　{t.Calls:N0} 次调用\n输入 {Tokens(t.Input)}　缓存读取 {Tokens(t.CacheRead)}　缓存写入 {Tokens(t.CacheWrite)}　输出 {Tokens(t.Output)}";

    private static TokenCounts Sum(IEnumerable<UsageRow> rows)
    {
        var total = new TokenCounts();
        foreach (var row in rows) total += row.Tokens;
        return total;
    }

    /// <summary>Token counts the way they are read aloud in Chinese: 1.23 亿, 4.5 万, 9,876.</summary>
    public static string Tokens(long n) => n switch
    {
        >= 100_000_000 => $"{n / 1e8:0.##} 亿",
        >= 10_000 => $"{n / 1e4:0.#} 万",
        _ => n.ToString("N0", CultureInfo.InvariantCulture),
    };
}
