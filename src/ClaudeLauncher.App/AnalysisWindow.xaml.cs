using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ClaudeLauncher.App.ViewModels;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Usage;
using Visibility = System.Windows.Visibility;

namespace ClaudeLauncher.App;

/// <summary>A pause after which the cache had expired, for the list.</summary>
public sealed record RewriteLine(string When, string Gap, string Tokens, string Model, string Tip);

/// <summary>A tool and what its results put into the context, for the list.</summary>
public sealed record ToolLine(string Name, string Count, string Size, string Detail, GridLength Bar, GridLength Rest, string Tip);

/// <summary>A line of calls the chart can show: the conversation itself, a subagent, a spawned task.</summary>
public sealed record SeriesChoice(string Label, AnalysisSeries Series)
{
    public override string ToString() => Label;
}

/// <summary>
/// How one conversation used its context, call by call — read from its transcripts when the
/// window opens, one window per conversation, nothing kept once it closes.
/// </summary>
public partial class AnalysisWindow : Window
{
    private static readonly Dictionary<ConversationKey, AnalysisWindow> OpenWindows = new();

    private readonly SessionMetadata _row;
    private readonly SourceInfo _source;
    private readonly string _path;
    private readonly CancellationTokenSource _closing = new();

    /// <summary>
    /// The title, cards, chart and note take about 570 at the narrowest width; shorter than this,
    /// the page scrolls rather than squeeze the lists to nothing.
    /// </summary>
    private const double BodyMinHeight = 740;

    public static void ShowFor(Window anchor, SessionRowViewModel row, string transcriptPath) =>
        ShowFor(anchor, row.Metadata, row.Title, transcriptPath);

    public static void ShowFor(Window anchor, SessionMetadata row, string title, string transcriptPath)
    {
        if (OpenWindows.TryGetValue(row.Key, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var window = new AnalysisWindow(row, title, transcriptPath);
        window.PlaceOver(anchor);
        OpenWindows[row.Key] = window;
        window.Closed += (_, _) =>
        {
            OpenWindows.Remove(row.Key);
            window._closing.Cancel();
            window.Release();
        };
        window.Show();
    }

    /// <summary>Why a conversation of this source cannot be analysed.</summary>
    public static string NoTokensText(SourceInfo source) =>
        $"「{source.ReportName}」的记录里没有逐次调用的 token：网页版的数据导出只有对话内容，这里分析不了。";

    private AnalysisWindow(SessionMetadata row, string title, string transcriptPath)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _row = row;
        _source = Sources.Describe(row.Key.Backend);
        _path = transcriptPath;
        Title = $"{title} · 分析";
        TitleLine.Text = title;
        TitleLine.ToolTip = title;
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

    /// <summary>What was read goes with the window: the lists, the chart's calls.</summary>
    private void Release()
    {
        Chart.Show(null);
        SeriesBox.ItemsSource = null;
        TipList.ItemsSource = null;
        RewriteList.ItemsSource = null;
        ToolList.ItemsSource = null;
    }

    private async Task LoadAsync()
    {
        if (!_source.CountsTokens)
        {
            SummaryLine.Text = "这个来源不记录 token";
            ShowEmpty(NoTokensText(_source));
            return;
        }
        var app = _source.Family;
        var codex = app == AppFamily.Codex;
        // A copy's history tells what the cache held when its own calls began; it is counted where it was made.
        var own = OwnPart.For(_row);
        // Which thread this is, from the row: the file read may be this tool's copy, named otherwise.
        var thread = codex ? _row.Key.NativeSessionId : null;
        var recorded = codex ? _row.NativeOpenRef : null;
        SessionAnalysis analysis;
        try
        {
            var cancel = _closing.Token;
            var path = _path;
            analysis = await Task.Run(() => SessionAnalyzer.Analyze(
                SessionAnalyzer.FilesFor(path, app, codex ? CodexPaths.Discover() : null, thread, recorded, cancel), own, cancel), cancel);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or System.Text.Json.JsonException)
        {
            SummaryLine.Text = "读不了这条会话的记录文件：" + ex.Message;
            return;
        }
        if (_closing.IsCancellationRequested) return;
        Show(analysis, own);
    }

    private void ShowEmpty(string text)
    {
        EmptyText.Text = text;
        EmptyText.Visibility = Visibility.Visible;
    }

    private void Show(SessionAnalysis analysis, OwnPart own)
    {
        var copied = _row.Key.Backend == ConversationKey.Codex ? "分叉" : "续接";
        var where = _row.Key.Backend == ConversationKey.Codex ? "任务" : "会话";
        var totals = analysis.Totals;
        if (totals.Calls == 0)
        {
            SummaryLine.Text = "没有找到记下 token 的调用";
            ShowEmpty(own.IsWhole
                ? "这条会话的记录里没有记下 token 的调用。"
                : $"这条{where}{copied}之后还没有记下 token 的调用；{copied}前的调用记在原{where}里。");
            return;
        }

        var helper = analysis.App == AppFamily.Codex ? "子任务" : "子代理";
        var mainCalls = analysis.Series.Where(s => s.Label == SessionAnalyzer.MainLabel).Sum(s => s.Calls.Count);
        var helpers = analysis.Series.Count(s => s.Label != SessionAnalyzer.MainLabel);
        SummaryLine.Text = $"{totals.Calls:N0} 次调用"
                           + (helpers > 0 ? $"（主线 {mainCalls:N0}，另有 {helpers} 条{helper}的线）" : "")
                           + (totals.First is { } first && totals.Last is { } last
                               ? $"　·　{AnalysisText.When(first)} – {AnalysisText.When(last)}" : "")
                           + (own.IsWhole ? "" : $"　·　只算{copied}之后的调用，{copied}前的在原{where}里")
                           + (analysis.Unread.Count > 0 ? $"　·　{analysis.Unread.Count} 个{helper}的记录读不了" : "");

        ShowCards(analysis, helper, mainCalls);
        ShowChart(analysis);
        ShowLists(analysis);
        Cards.Visibility = ChartSection.Visibility = Lists.Visibility = Visibility.Visible;
        Body.MinHeight = BodyMinHeight;
    }

    private void ShowCards(SessionAnalysis analysis, string helper, int mainCalls)
    {
        var totals = analysis.Totals;
        CallsValue.Text = totals.Calls.ToString("N0", CultureInfo.InvariantCulture);
        CallsNote.Text = mainCalls == totals.Calls ? "都在主线上" : $"主线 {mainCalls:N0}，{helper} {totals.Calls - mainCalls:N0}";
        TotalValue.Text = AnalysisText.Tokens(totals.Total);
        MaxValue.Text = AnalysisText.Tokens(totals.MaxContext);
        var largest = analysis.Series.SelectMany(s => s.Calls.Select(c => (Call: c, s.Label))).MaxBy(x => x.Call.Context);
        MaxNote.Text = AnalysisText.When(largest.Call.At) + (largest.Label == SessionAnalyzer.MainLabel ? "" : " · " + largest.Label);
        MaxNote.ToolTip = MaxNote.Text;
        AverageValue.Text = AnalysisText.Tokens((long)totals.AverageContext);
        HitValue.Text = AnalysisText.Share(totals.CacheRead, totals.Context);
        HitNote.Text = $"缓存读取 {AnalysisText.Tokens(totals.CacheRead)}";

        var rewrites = analysis.Rewrites;
        RewritesValue.Text = $"{rewrites.Count} 次";
        RewritesNote.Text = rewrites.Count > 0 ? $"重来了 {AnalysisText.Tokens(analysis.RewrittenTokens)} token" : "停顿后缓存都还在";
        RewritesCard.ToolTip = analysis.App == AppFamily.Claude
            ? "离开超过缓存保留的时间（5 分钟，写的是 1 小时缓存时按 1 小时）后继续，缓存已经没了：整段上下文按缓存写入价重新写了一遍"
            : "离开超过 5 分钟后继续，缓存已经没了：整段上下文按全价重新读入了一遍";

        var compactions = analysis.Compactions;
        var marked = compactions.Count(c => c.Marked);
        CompactionsValue.Text = $"{compactions.Count} 次";
        CompactionsNote.Text = compactions.Count == 0 ? "上下文没有被压缩过"
            : marked == compactions.Count ? "都是应用记下的"
            : marked == 0 ? "按上下文突然减半判断"
            : $"{compactions.Count - marked} 次按上下文减半判断";
        CompactionsCard.ToolTip = compactions.Count == 0
            ? "应用记下的压缩（Claude Code 的 /compact 或自动压缩、Codex 的 compacted），或上下文一下子少了一半以上"
            : string.Join("\n", compactions.Select(c =>
                $"{AnalysisText.When(c.At)}　{AnalysisText.Tokens(c.Before)}"
                + (c.After is { } after ? $" → {AnalysisText.Tokens(after)}" : " → （之后还没有调用）")
                + (c.Marked ? "" : "　按上下文减半判断")
                + (c.Series == SessionAnalyzer.MainLabel ? "" : "　" + c.Series)));

        DurationValue.Text = AnalysisText.Pause(totals.Duration);
        DurationNote.Text = totals.First is { } first && totals.Last is { } last
            ? $"{AnalysisText.When(first)} 起" + (last.ToLocalTime().Date == first.ToLocalTime().Date ? "" : $"，到 {AnalysisText.When(last)}")
            : "";
        DurationNote.ToolTip = DurationNote.Text;
        OutputValue.Text = AnalysisText.Tokens(totals.Output);

        var results = analysis.Tools.Sum(t => t.Results);
        ToolsValue.Text = results == 0 ? "—"
            : "≈ " + AnalysisText.Tokens((long)Math.Round(analysis.ToolBytes / ToolLoad.BytesPerToken));
        ToolsNote.Text = results == 0 ? "没有工具结果" : $"{results:N0} 次，{AnalysisText.Bytes(analysis.ToolBytes)}，估算";
    }

    private void ShowChart(SessionAnalysis analysis)
    {
        var choices = analysis.Series
            .Select(s => new SeriesChoice($"{s.Label}　{s.Calls.Count:N0} 次", s)).ToList();
        SeriesBox.ItemsSource = choices;
        SeriesBox.Visibility = choices.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        SeriesBox.SelectedIndex = 0;            // shows the conversation's own line first
    }

    private void Series_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ShowSeries((SeriesBox.SelectedItem as SeriesChoice)?.Series);

    private void ShowSeries(AnalysisSeries? series)
    {
        Chart.Show(series?.Calls);
        if (series is null)
        {
            ChartNote.Text = "";
            return;
        }
        var gaps = series.Calls.Count(c => c.LongGap);
        ChartNote.Text = $"最高 {AnalysisText.Tokens(series.MaxContext)}"
                         + (gaps > 0 ? $"　·　{gaps} 次长时间停顿" : "")
                         + "　·　指向柱子看那次调用";
    }

    private void ShowLists(SessionAnalysis analysis)
    {
        TipList.ItemsSource = analysis.Tips;

        var claude = analysis.App == AppFamily.Claude;
        RewriteList.ItemsSource = analysis.Rewrites.Select(r => new RewriteLine(
            AnalysisText.When(r.At),
            $"　隔了 {AnalysisText.Pause(r.Gap)}",
            AnalysisText.Tokens(r.Tokens),
            r.Model + (r.Series == SessionAnalyzer.MainLabel ? "" : " · " + r.Series),
            $"上一次调用的上下文是 {AnalysisText.Tokens(r.ContextBefore)}；隔了 {AnalysisText.Pause(r.Gap)}，"
            + $"超过了缓存保留的 {AnalysisText.Pause(r.Kept)}，缓存已经过期，"
            + $"这次{(claude ? "按缓存写入价重新写了" : "按全价重新读入了")} {AnalysisText.Tokens(r.Tokens)} token")).ToList();
        NoRewrites.Visibility = analysis.Rewrites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var all = analysis.ToolBytes;
        var top = Math.Max(1, analysis.Tools.Count == 0 ? 1 : analysis.Tools[0].Bytes);
        ToolList.ItemsSource = analysis.Tools.Select(t =>
        {
            var share = (double)t.Bytes / top;
            return new ToolLine(t.Name, $"　{t.Results:N0} 次",
                $"≈ {AnalysisText.Tokens(t.EstimatedTokens)}",
                $"{AnalysisText.Bytes(t.Bytes)}，占 {AnalysisText.Share(t.Bytes, all)}　·　最大一次 {AnalysisText.Bytes(t.Largest)}",
                new GridLength(share, GridUnitType.Star), new GridLength(1 - share, GridUnitType.Star),
                $"{t.Name}：{t.Results:N0} 次结果，共 {AnalysisText.Bytes(t.Bytes)}（约 {AnalysisText.Tokens(t.EstimatedTokens)} token，按字节估算），"
                + $"占工具结果的 {AnalysisText.Share(t.Bytes, all)}；最大的一次 {AnalysisText.Bytes(t.Largest)}");
        }).ToList();
        NoTools.Visibility = analysis.Tools.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
