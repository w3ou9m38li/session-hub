using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClaudeLauncher.App.ViewModels;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Changes;
using ClaudeLauncher.Infrastructure.Compare;
using ClaudeLauncher.Infrastructure.Usage;
using Visibility = System.Windows.Visibility;

namespace ClaudeLauncher.App;

/// <summary>A side's heading over its column.</summary>
public sealed record CompareHeader(string Side, string Source, Brush Badge, string Title, string Project,
    string Times, string Amounts, string? AmountsTip);

/// <summary>A row of the comparison: the left turn, how alike the prompts are, the right turn.</summary>
public sealed record CompareRow(TurnView A, TurnView B, TurnMatch Match, string Marker, Brush MarkerBrush, string? MarkerTip);

/// <summary>A changed file in one of the three lists.</summary>
public sealed record CompareFile(string Name, string Tag, string Added, string Removed, string Detail, string Tip)
{
    public bool HasDetail => Detail.Length > 0;
}

/// <summary>
/// One side of a row: a prompt and its answer, each cut to a few lines' worth until 「展开」 — or
/// nothing, when this side never asked it.
/// </summary>
public sealed class TurnView : INotifyPropertyChanged
{
    /// <summary>A prompt is shown whole up to this; an answer up to <see cref="AnswerShown"/>.</summary>
    private const int PromptShown = 1500, AnswerShown = 600;

    /// <summary>Text barely longer than what is shown is shown whole: 「展开」 for a few words is no help.</summary>
    private const int Slack = 80;

    private readonly CompareTurn? _turn;
    private readonly string _assistant;
    private bool _promptOpen, _answerOpen;

    public TurnView(CompareTurn? turn, string assistant, Brush stripe, string missing)
    {
        _turn = turn;
        _assistant = assistant;
        Stripe = stripe;
        Missing = missing;
    }

    public bool HasTurn => _turn is not null;

    /// <summary>What an empty side says.</summary>
    public string Missing { get; }

    /// <summary>The app's colour, down the left edge.</summary>
    public Brush Stripe { get; }

    public string Heading => _turn is null ? ""
        : !_turn.HasPrompt ? "开头，提问之前"
        : $"第 {_turn.Number} 条提问" + (_turn.At is { } at ? "　·　" + CompareWindow.Moment(at) : "");

    public string Prompt => _turn is null ? ""
        : !_turn.HasPrompt ? "（还没有提问，助手先说了话）"
        : _promptOpen ? _turn.Prompt : Head(_turn.Prompt, PromptShown);

    public bool PromptCut => _turn is not null && _turn.Prompt.Length > PromptShown + Slack;

    public string PromptToggle => _promptOpen ? "收起" : $"展开（共 {_turn?.PromptLength ?? 0:N0} 字）";

    public string AnswerHeading
    {
        get
        {
            if (_turn is null) return "";
            if (_turn.Answer.Length == 0) return _assistant + "　·　没有回答";
            var when = _turn.AnsweredAt is { } at ? "　·　" + CompareWindow.Moment(at) : "";
            var took = _turn.At is { } asked && _turn.AnsweredAt is { } done && done > asked
                ? $"（用了 {CompareWindow.Duration(done - asked)}）"
                : "";
            return _assistant + when + took;
        }
    }

    public string Answer => _turn is null || _turn.Answer.Length == 0 ? ""
        : _answerOpen ? _turn.Answer : Head(_turn.Answer, AnswerShown);

    public bool AnswerCut => _turn is not null && _turn.Answer.Length > AnswerShown + Slack;

    public string AnswerToggle => _answerOpen ? "收起" : $"展开（共 {_turn?.AnswerLength ?? 0:N0} 字）";

    public void TogglePrompt()
    {
        _promptOpen = !_promptOpen;
        Changed(nameof(Prompt), nameof(PromptToggle));
    }

    public void ToggleAnswer()
    {
        _answerOpen = !_answerOpen;
        Changed(nameof(Answer), nameof(AnswerToggle));
    }

    private static string Head(string text, int shown)
    {
        if (text.Length <= shown + Slack) return text;
        var cut = char.IsHighSurrogate(text[shown - 1]) ? shown - 1 : shown;
        return text[..cut] + "…";
    }

    private void Changed(params string[] names)
    {
        foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Two conversations side by side: their prompts lined up — the same request on one row, one
/// asked on one side alone on a row of its own — each with the answer it got; each side's time,
/// prompts, tokens and changed files. Read when the window opens, from the transcripts alone;
/// nothing is kept once it closes. One window per pair, the older conversation on the left.
/// </summary>
public partial class CompareWindow : Window
{
    private static readonly Dictionary<(ConversationKey, ConversationKey), CompareWindow> OpenWindows = new();

    private const string LeftSide = "左 · 较早", RightSide = "右 · 较新";

    private SessionRowViewModel _a, _b;
    private string _pathA, _pathB;
    private (ConversationKey, ConversationKey) _key;
    private readonly Func<SessionRowViewModel, Task<TokenCounts?>>? _tokensOf;
    private readonly CancellationTokenSource _closing = new();
    private List<CompareRow> _rows = [];
    private ScrollViewer? _turnsScroll;

    /// <summary>The comparison is on screen: the filter has rows to work on.</summary>
    private bool _shown;

    /// <summary>The read failed: asking for the pair again reads it again.</summary>
    private bool _failed;

    /// <param name="a">The older conversation: shown on the left.</param>
    /// <param name="tokensOf">The usage ledger's count for a conversation, when there is a ledger; else tokens are read from the transcripts.</param>
    public static void ShowFor(Window anchor, SessionRowViewModel a, string pathA, SessionRowViewModel b, string pathB,
        Func<SessionRowViewModel, Task<TokenCounts?>>? tokensOf = null)
    {
        var key = (a.Metadata.Key, b.Metadata.Key);
        // The same two either way round: a conversation that went on since may now be the newer one.
        if (OpenWindows.TryGetValue(key, out var existing) || OpenWindows.TryGetValue((key.Item2, key.Item1), out existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            // A file being written just as it was read reads fine a moment later: asking again tries again.
            if (existing._failed) existing.Retry(a, pathA, b, pathB);
            return;
        }
        var window = new CompareWindow(a, pathA, b, pathB, tokensOf);
        window.PlaceOver(anchor);
        OpenWindows[key] = window;
        window.Closed += (_, _) =>
        {
            OpenWindows.Remove(window._key);
            window._closing.Cancel();
            window.Release();
        };
        window.Show();
    }

    private CompareWindow(SessionRowViewModel a, string pathA, SessionRowViewModel b, string pathB,
        Func<SessionRowViewModel, Task<TokenCounts?>>? tokensOf)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        WindowPlacementMemory.Restore(this, AppSettings.Current.CompareWindow, restorePosition: false);
        _tokensOf = tokensOf;
        Take(a, pathA, b, pathB);
        Loaded += async (_, _) => await LoadAsync();
        Closing += (_, _) => AppSettings.Update(s => s with { CompareWindow = WindowPlacementMemory.Capture(this) });
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    /// <summary>The pair this window compares: its rows and transcripts, its title, and its headings until the read is done.</summary>
    [MemberNotNull(nameof(_a), nameof(_b), nameof(_pathA), nameof(_pathB))]
    private void Take(SessionRowViewModel a, string pathA, SessionRowViewModel b, string pathB)
    {
        _a = a;
        _b = b;
        _pathA = pathA;
        _pathB = pathB;
        _key = (a.Metadata.Key, b.Metadata.Key);
        Title = $"{a.Title}  ⇄  {b.Title} · 并排对比";
        HeaderA.Content = Header(a, LeftSide, null);
        HeaderB.Content = Header(b, RightSide, null);
    }

    /// <summary>Reads the pair again after a failed read, as the list has it now: a path, or which one is older, may have changed.</summary>
    private void Retry(SessionRowViewModel a, string pathA, SessionRowViewModel b, string pathB)
    {
        OpenWindows.Remove(_key);
        Take(a, pathA, b, pathB);
        OpenWindows[_key] = this;
        SummaryLine.Text = "正在读取两段对话…";
        SummaryLine.ToolTip = null;
        _ = LoadAsync();
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

    /// <summary>What the window read goes with it: nothing of it is kept anywhere else.</summary>
    private void Release()
    {
        TurnList.ItemsSource = null;
        OnlyAList.ItemsSource = OnlyBList.ItemsSource = BothList.ItemsSource = null;
        _rows = [];
    }

    private async Task LoadAsync()
    {
        _failed = false;
        var cancel = _closing.Token;
        SessionComparison result;
        List<CompareRow> rows;
        try
        {
            var known = await Task.WhenAll(LedgerTokens(_a), LedgerTokens(_b));
            if (cancel.IsCancellationRequested) return;
            var sideA = new ConversationSide(_pathA, _a.Metadata.Key.Backend, OwnPart.For(_a.Metadata), _a.ProjectRef, known[0]);
            var sideB = new ConversationSide(_pathB, _b.Metadata.Key.Backend, OwnPart.For(_b.Metadata), _b.ProjectRef, known[1]);
            (result, rows) = await Task.Run(() =>
            {
                var compared = SessionComparer.Compare(sideA, sideB, cancel);
                return (compared, Rows(compared, sideA, sideB));
            }, cancel);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (UnreadableSideException ex)
        {
            ShowUnread(ex switch
            {
                { A: { } x, B: { } y } => x.Message == y.Message
                    ? "两边的记录文件都读不了：" + x.Message
                    : $"两边的记录文件都读不了。左边：{x.Message}；右边：{y.Message}",
                { A: { } x } => "读不了左边这条会话的记录文件：" + x.Message,
                _ => "读不了右边这条会话的记录文件：" + ex.Message,
            }, ex.A is not null, ex.B is not null);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowUnread("读不了这两条会话的记录文件：" + ex.Message, true, true);
            return;
        }
        if (cancel.IsCancellationRequested) return;
        Present(result, rows);
    }

    /// <summary>
    /// A read that failed: the side that could not be read says so, the other that it could.
    /// The window stays for the pair, and asking for the pair again reads both again.
    /// </summary>
    private void ShowUnread(string why, bool a, bool b)
    {
        _failed = true;
        HeaderA.Content = Header(_a, LeftSide, null, a ? "没有读出来" : "这边能读，另一边没读出来");
        HeaderB.Content = Header(_b, RightSide, null, b ? "没有读出来" : "这边能读，另一边没读出来");
        SummaryLine.Text = why + "　·　再点一次「并排对比」（Ctrl+D）会重新读";
        SummaryLine.ToolTip = SummaryLine.Text;
    }

    /// <summary>The ledger's count, when the caller has a ledger and the source counts tokens at all.</summary>
    private async Task<TokenCounts?> LedgerTokens(SessionRowViewModel row) =>
        _tokensOf is not null && row.Source.CountsTokens ? await _tokensOf(row) : null;

    private static List<CompareRow> Rows(SessionComparison result, ConversationSide a, ConversationSide b)
    {
        var stripeA = Palette.BarOf(a.App);
        var stripeB = Palette.BarOf(b.App);
        return result.Rows.Select(row => new CompareRow(
                new TurnView(row.A, a.AssistantName, stripeA, "左边没有问过这一条"),
                new TurnView(row.B, b.AssistantName, stripeB, "右边没有问过这一条"),
                row.Match,
                row.Match switch
                {
                    TurnMatch.Identical => "相同",
                    TurnMatch.Similar => $"相近\n{row.Percent}%",
                    _ => "",
                },
                row.Match == TurnMatch.Identical ? Palette.Success : Palette.Info,
                row.Match != TurnMatch.Similar ? null
                : row.OpeningOnly
                    ? $"提问较长，只比较了开头 {PromptAligner.ComparedChars} 字：约 {row.Percent}% 的字相同（按相邻两字计）；"
                      + "后面的没有比较，两条提问并不完全一样"
                    : $"两边的提问有 {row.Percent}% 的字相同（按相邻两字计）"))
            .ToList();
    }

    // ------------------------------------------------------------------ showing

    private void Present(SessionComparison result, List<CompareRow> rows)
    {
        HeaderA.Content = Header(_a, LeftSide, result.A);
        HeaderB.Content = Header(_b, RightSide, result.B);

        SummaryLine.Text = $"左边 {result.A.Prompts} 条提问，右边 {result.B.Prompts} 条：相同 {result.Identical}"
                           + $"　·　相近 {result.Similar}　·　只在左边 {result.OnlyA}　·　只在右边 {result.OnlyB}"
                           + (result.Approximate ? "　·　提问太多，相近的提问可能没有对上" : "");
        SummaryLine.ToolTip = SummaryLine.Text;
        _rows = rows;
        _shown = true;
        ShowRows();

        if (result.Files is { } files)
        {
            FilesView.IsEnabled = true;
            FilesView.ToolTip = null;
            FilesView.Content = $"改动的文件（{files.OnlyA.Count + files.OnlyB.Count + files.Both.Count}）";
            ShowFiles(files, result.A.Files is not null, result.B.Files is not null);
        }
        else FilesView.ToolTip = "两边的来源都不记录文件改动";

        var tokensFrom = new[] { result.A.TokensFrom, result.B.TokensFrom };
        FooterLine.Text = "只比较对话：你的提问和助手的回答，工具调用和输出不比较。续接或分叉出来的会话只算它自己的那部分，复制来的历史算在原会话里。"
                          + $"「相近」是两条提问有六成以上的字相同（按相邻两字计；长提问只比开头 {PromptAligner.ComparedChars} 字）。"
                          + (tokensFrom.Contains(TokenSource.Ledger) ? "token 取自用量统计，含子代理。" : "")
                          + (tokensFrom.Contains(TokenSource.Transcript) ? "没进用量统计的从记录文件里现数，不含子代理。" : "")
                          + (result.Files is null ? "" : "改动的文件按在各自工作目录里的位置对上，只算成功的改动，不含子代理、子任务做的。");
    }

    private void ShowRows()
    {
        var shown = OnlyDifferent.IsChecked == true ? _rows.Where(r => r.Match != TurnMatch.Identical).ToList() : _rows;
        TurnList.ItemsSource = shown;
        var empty = _rows.Count == 0 ? "两条会话里都没有找到你的提问"
            : shown.Count == 0 ? "两边的提问全都一样，没有对不上的"
            : null;
        EmptyText.Text = empty ?? "";
        EmptyText.Visibility = empty is not null && TurnsView.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowFiles(FileComparison files, bool aRecords, bool bRecords)
    {
        OnlyAHeading.Text = $"只在左边改过（{files.OnlyA.Count}）";
        OnlyBHeading.Text = $"只在右边改过（{files.OnlyB.Count}）";
        BothHeading.Text = $"两边都改过（{files.Both.Count}）";
        OnlyANote.Text = aRecords ? files.OnlyA.Count == 0 ? "没有" : "" : _a.Source.NoChangesSummary ?? "这个来源不记录文件改动";
        OnlyBNote.Text = bRecords ? files.OnlyB.Count == 0 ? "没有" : "" : _b.Source.NoChangesSummary ?? "这个来源不记录文件改动";
        BothNote.Text = !(aRecords && bRecords) ? "有一边不记录文件改动，没法对照"
            : files.Both.Count == 0 ? "没有两边都改过的文件" : "";
        foreach (var note in new[] { OnlyANote, OnlyBNote, BothNote })
            note.Visibility = note.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        OnlyAList.ItemsSource = files.OnlyA.Select(Entry).ToList();
        OnlyBList.ItemsSource = files.OnlyB.Select(Entry).ToList();
        BothList.ItemsSource = files.Both.Select(f => new CompareFile(f.Key, KindTag(f.A.Kind == f.B.Kind ? f.A.Kind : ChangeKind.Updated), "", "",
            $"左 +{f.A.Added:N0} −{f.A.Removed:N0}{Times(f.A)}　·　右 +{f.B.Added:N0} −{f.B.Removed:N0}{Times(f.B)}",
            f.A.FullPath.Equals(f.B.FullPath, StringComparison.OrdinalIgnoreCase) ? f.A.FullPath : $"左：{f.A.FullPath}\n右：{f.B.FullPath}"))
            .ToList();

        static CompareFile Entry(ChangedPath f) =>
            new(f.Key, KindTag(f.Kind), $"+{f.Added:N0}", $"−{f.Removed:N0}", f.Times > 1 ? $"改了 {f.Times} 次" : "", f.FullPath);

        static string Times(ChangedPath f) => f.Times > 1 ? $"（{f.Times} 次）" : "";

        static string KindTag(ChangeKind kind) => kind switch
        {
            ChangeKind.Added => "　新建",
            ChangeKind.Deleted => "　已删除",
            _ => "",
        };
    }

    private void OnlyDifferent_Changed(object sender, RoutedEventArgs e)
    {
        if (_shown) ShowRows();
    }

    private void View_Checked(object sender, RoutedEventArgs e)
    {
        if (TurnList is null) return;           // while the window is being built
        var turns = TurnsView.IsChecked == true;
        TurnList.Visibility = turns ? Visibility.Visible : Visibility.Collapsed;
        FilesPanel.Visibility = turns ? Visibility.Collapsed : Visibility.Visible;
        OnlyDifferent.Visibility = turns ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = turns && EmptyText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Paging keys page the turns wherever the focus is: a prompt or answer the user clicked into
    /// would otherwise keep them to itself, and with nothing focused yet they would go nowhere.
    /// With Shift they still select text; Home and End alone still move in a text the user is in.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (TurnList.Visibility == Visibility.Visible
            && (_turnsScroll ??= FindDescendant<ScrollViewer>(TurnList)) is { } scroll)
        {
            var mods = Keyboard.Modifiers;
            var inText = Keyboard.FocusedElement is TextBox;
            switch (e.Key)
            {
                case Key.PageDown when mods == ModifierKeys.None:
                    scroll.PageDown();
                    e.Handled = true;
                    break;
                case Key.PageUp when mods == ModifierKeys.None:
                    scroll.PageUp();
                    e.Handled = true;
                    break;
                case Key.End when mods == ModifierKeys.Control || (mods == ModifierKeys.None && !inText):
                    scroll.ScrollToEnd();
                    e.Handled = true;
                    break;
                case Key.Home when mods == ModifierKeys.Control || (mods == ModifierKeys.None && !inText):
                    scroll.ScrollToHome();
                    e.Handled = true;
                    break;
            }
        }
        base.OnPreviewKeyDown(e);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void TogglePrompt_Click(object sender, RoutedEventArgs e) =>
        ((sender as FrameworkElement)?.DataContext as TurnView)?.TogglePrompt();

    private void ToggleAnswer_Click(object sender, RoutedEventArgs e) =>
        ((sender as FrameworkElement)?.DataContext as TurnView)?.ToggleAnswer();

    // ------------------------------------------------------------------ words

    /// <summary>A side's heading: before the read, what the row already says; after it, what the read found.</summary>
    private static CompareHeader Header(SessionRowViewModel row, string side, SideSummary? summary, string pending = "正在读取…")
    {
        var project = row.ProjectRef is { Length: > 0 } folder ? folder : "（没有记录工作目录）";
        var source = row.Source.SectionName;
        if (summary is null)
            return new CompareHeader(side, source, row.BackendFill, row.Title, project, "　", pending, null);

        var times = summary is { First: { } first, Last: { } last }
            ? $"{Moment(first)} → {(SameDay(first, last) ? last.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) : Moment(last))}"
              + (summary.Span is { } span && span > TimeSpan.Zero ? $"　·　{Duration(span)}" : "")
            : "记录里没有时间";
        var parts = new List<string> { $"你的提问 {summary.Prompts} 条" };
        string? tip = null;
        if (summary.Tokens is { } t)
        {
            parts.Add(t.Calls > 0
                ? $"token {UsageWindow.Tokens(t.Total)}（每次上下文 {UsageWindow.Tokens((long)t.ContextPerCall)}）"
                : "token 0");
            tip = $"{t.Calls:N0} 次调用　·　合计 {UsageWindow.Tokens(t.Total)}\n"
                  + $"输入 {UsageWindow.Tokens(t.Input)}　缓存读取 {UsageWindow.Tokens(t.CacheRead)}　"
                  + $"缓存写入 {UsageWindow.Tokens(t.CacheWrite)}　输出 {UsageWindow.Tokens(t.Output)}\n"
                  + (summary.TokensFrom == TokenSource.Ledger
                      ? "取自用量统计：含子代理；续接、分叉来的调用算在原会话里"
                      : "从记录文件里现数的：只算这一份记录（不含子代理），续接、分叉来的只算之后的");
        }
        else parts.Add("不记录 token");
        if (summary.Files is { } files) parts.Add($"改了 {files.Count} 个文件");
        return new CompareHeader(side, source, row.BackendFill, row.Title, project, times, string.Join("　·　", parts),
            tip ?? "这个来源的记录里没有 token 数");
    }

    /// <summary>A moment as the window shows it: 「10月3日 14:05」, with the year when it is not this one.</summary>
    internal static string Moment(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        return local.ToString(local.Year == DateTime.Now.Year ? "M月d日 HH:mm" : "yyyy年M月d日 HH:mm", CultureInfo.InvariantCulture);
    }

    private static bool SameDay(DateTimeOffset a, DateTimeOffset b) => a.ToLocalTime().Date == b.ToLocalTime().Date;

    /// <summary>A length of time in words: 「40 秒」「3 分钟」「2 小时 5 分」「3 天 4 小时」.</summary>
    internal static string Duration(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => $"{Math.Max(1, (int)span.TotalSeconds)} 秒",
        { TotalHours: < 1 } => $"{(int)span.TotalMinutes} 分钟",
        { TotalDays: < 1 } => span.Minutes > 0 ? $"{(int)span.TotalHours} 小时 {span.Minutes} 分" : $"{(int)span.TotalHours} 小时",
        _ => span.Hours > 0 ? $"{(int)span.TotalDays} 天 {span.Hours} 小时" : $"{(int)span.TotalDays} 天",
    };
}
