using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClaudeLauncher.App.ViewModels;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Core.Display;
using ClaudeLauncher.Infrastructure;
using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Launch;
using Microsoft.Win32;
using Visibility = System.Windows.Visibility;

namespace ClaudeLauncher.App;

/// <summary>One transcript entry on screen. Bulky detail (tool text, folded context) opens on demand.</summary>
public sealed class MessageViewModel : INotifyPropertyChanged
{
    // The theme's colours, read when a message is made: the theme is chosen before any is.
    private static Brush UserBg => Palette.UserBubble;
    private static Brush AssistantBg => Palette.AssistantBubble;
    private static Brush UserAccent => Palette.ClaudeText;
    private static Brush AssistantAccent => Palette.Info;
    private static Brush Normal => Palette.Text;

    private static readonly CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;

    /// <summary>What a person means by "the same text": case, full/half width and kana type ignored.</summary>
    private const CompareOptions Loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth | CompareOptions.IgnoreKanaType;

    private bool _isExpanded;
    private bool _isMatch;
    private bool _isCurrentMatch;

    public MessageViewModel(TranscriptEntry entry, string assistantName, bool expandTools)
    {
        Entry = entry;
        _isExpanded = expandTools && entry.IsTool;

        (Speaker, Background, Accent) = entry.Role switch
        {
            MessageRole.User => ("你", UserBg, UserAccent),
            MessageRole.Assistant => (assistantName, AssistantBg, AssistantAccent),
            MessageRole.ToolCall => ("工具调用", Brushes.Transparent, Brushes.Transparent),
            MessageRole.ToolResult => ("工具输出", Brushes.Transparent, Brushes.Transparent),
            // A picture is almost always one the user sent: it sits on their side, in their colour.
            MessageRole.Attachment when entry.Image is not null => ("你发送的图片", UserBg, UserAccent),
            MessageRole.Attachment => ("附件", Brushes.Transparent, Brushes.Transparent),
            _ => ("系统", Brushes.Transparent, Brushes.Transparent),
        };
    }

    public TranscriptEntry Entry { get; }
    public string Speaker { get; }
    public string Text => Entry.Text;

    /// <summary>The assistant writes Markdown and is shown rendered; the user's words are shown as typed.</summary>
    public bool RendersMarkdown => Entry.Role == MessageRole.Assistant;

    private IReadOnlyList<MdBlock>? _blocks;

    /// <summary>Longest text a message is laid out with; laying out millions of characters would freeze the window.</summary>
    public const int MaxShownChars = 100_000;

    /// <summary>What the bubble shows: the whole message, or — for an enormous one — its beginning and a note.</summary>
    public string ShownText => Text.Length <= MaxShownChars ? Text
        : Text[..MaxShownChars] + $"\n\n…（这条消息共 {Text.Length:N0} 字，只显示前 {MaxShownChars:N0} 字；「复制全部对话」和「导出」含全文）";

    /// <summary>Parsed once, the first time the message is shown; scrolling back to it costs no parse.</summary>
    public IReadOnlyList<MdBlock> Blocks => _blocks ??= Markdown.Parse(ShownText);

    private string? _highlightTerm;

    /// <summary>The find word while this message holds it: marked in the text itself.</summary>
    public string? HighlightTerm
    {
        get => _highlightTerm;
        set
        {
            if (_highlightTerm == value) return;
            _highlightTerm = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HighlightTerm)));
        }
    }
    public string? Detail => Entry.Detail;
    public string? Timestamp => Entry.Timestamp;
    public bool HasDetail => !string.IsNullOrEmpty(Entry.Detail);
    public bool IsTool => Entry.IsTool;

    /// <summary>What the two sides said, as opposed to tool traffic and app scaffolding.</summary>
    public bool IsDialogue => Entry.Role is MessageRole.User or MessageRole.Assistant;

    /// <summary>A picture or file sent with a message: part of the conversation, shown in 「只看对话」 too.</summary>
    public bool IsAttachment => Entry.Role == MessageRole.Attachment;

    public bool HasImage => Entry.Image is not null;

    private ImageSource? _thumbnail;
    private bool _thumbnailRequested;
    private bool _thumbnailFailed;

    /// <summary>
    /// The picture, scaled down for the list. Decoded off the UI thread the first time the row
    /// is shown, and null until then; a picture Windows cannot read leaves it null for good.
    /// </summary>
    public ImageSource? Thumbnail
    {
        get
        {
            if (_thumbnail is null && !_thumbnailRequested && Entry.Image is { } bytes)
            {
                _thumbnailRequested = true;
                _ = LoadThumbnailAsync(bytes);
            }
            return _thumbnail;
        }
    }

    public bool ThumbnailFailed => _thumbnailFailed;

    private async Task LoadThumbnailAsync(byte[] bytes)
    {
        var picture = await Task.Run(() => Pictures.Decode(bytes, maxWidth: 720));
        _thumbnail = picture;
        _thumbnailFailed = picture is null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThumbnailFailed)));
    }

    public Brush Background { get; }
    public Brush Accent { get; }
    public Brush Foreground => Normal;
    public string FontFamily => "Microsoft YaHei UI, Segoe UI";

    /// <summary>Your messages sit in from the left and the assistant's from the right, the way every chat reads.</summary>
    public Thickness BubbleMargin => Entry.Role == MessageRole.User
        ? new Thickness(64, 6, 12, 6)
        : new Thickness(12, 6, 64, 6);

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    public bool IsMatch
    {
        get => _isMatch;
        set => Set(ref _isMatch, value);
    }

    public bool IsCurrentMatch
    {
        get => _isCurrentMatch;
        set => Set(ref _isCurrentMatch, value);
    }

    private bool _isSelected;

    /// <summary>Chosen for 「复制选中的」/「导出选中的…」. Kept here, not on the bubble, which the list recycles.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    private bool _justCopied;

    /// <summary>
    /// For a moment after its copy button was pressed: the button shows a tick. Kept here and not
    /// on the button, which the list recycles for other messages while it scrolls.
    /// </summary>
    public bool JustCopied
    {
        get => _justCopied;
        set => Set(ref _justCopied, value);
    }

    public bool TextContains(string term) => Compare.IndexOf(Text, term, Loose) >= 0;

    public bool DetailContains(string term) => Detail is not null && Compare.IndexOf(Detail, term, Loose) >= 0;

    /// <summary>
    /// Whether a search for <paramref name="term"/> should stop here. What is shown is searched;
    /// tool arguments and output only when the reader asked to see them, and the folded app
    /// context never — it is the same boilerplate in every conversation and would bury the
    /// real hits under it.
    /// </summary>
    public bool Matches(string term, bool includeToolDetail) =>
        TextContains(term) || (includeToolDetail && IsTool && DetailContains(term));

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(ref bool field, bool value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>Bubbles for the dialogue, pictures as pictures, a single foldable line for everything else.</summary>
public sealed class TranscriptTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Bubble { get; set; }
    public DataTemplate? Compact { get; set; }
    public DataTemplate? Plain { get; set; }
    public DataTemplate? Picture { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item switch
    {
        MessageViewModel { HasImage: true } => Picture,
        MessageViewModel { IsDialogue: true } => Bubble,
        MessageViewModel { HasDetail: true } => Compact,
        _ => Plain,
    };
}

public partial class TranscriptWindow : Window
{
    /// <summary>Entries per page while browsing: enough to fill several screens, small enough to arrive at once.</summary>
    private const int PageSize = 300;

    /// <summary>Entries per page when reading on to the end for "最新" or a search.</summary>
    private const int BulkPageSize = 2000;

    /// <summary>
    /// From this size on, 「最新」 first shows the end read straight from the end of the file,
    /// while the whole conversation is read behind it; smaller files are read whole at once.
    /// </summary>
    private const long TailFirstBytes = 8L * 1024 * 1024;

    /// <summary>Entries shown from the end while the rest is read.</summary>
    private const int TailEntries = 150;

    /// <summary>One window per conversation: opening it again brings the existing one forward.</summary>
    private static readonly Dictionary<ConversationKey, TranscriptWindow> OpenWindows = new();

    private readonly TranscriptReader _reader;
    private readonly SessionRowViewModel _row;
    private readonly string _path;
    private readonly ObservableCollection<MessageViewModel> _messages = [];
    private readonly ICollectionView _view;
    private readonly CancellationTokenSource _closing = new();
    private readonly DispatcherTimer _findDelay = new() { Interval = TimeSpan.FromMilliseconds(220) };

    /// <summary>The raw read on the thread pool: what still holds the reader when the window closes.</summary>
    private Task? _pendingRead;

    /// <summary>Reading and appending one page, end to end.</summary>
    private Task<bool>? _loading;

    private int _offset;
    private bool _reachedEnd;
    private int _toolOutputs;
    private long _bytePosition;
    private CancellationTokenSource? _bulk;
    private Action? _statusAction;

    private readonly List<MessageViewModel> _matches = [];
    private int _currentMatch = -1;
    private string _findTerm = string.Empty;

    /// <summary>Which place of the word in the current message F3 is at, and how many it has — known once it is shown.</summary>
    private int _place;
    private int _places;

    private ScrollViewer? _scroll;

    /// <summary>
    /// Opens the conversation in a window of its own — or brings forward the one already open.
    ///
    /// <para>Deliberately not owned by the list's window. An owned window minimises with its
    /// owner and can never go behind it; a reader has to stay open while the list is put
    /// away, and sit wherever the user moves it. So it gets its own taskbar button and its
    /// own minimise, and is only placed over the list when it first opens.</para>
    /// </summary>
    /// <param name="jumpToLatest">Open at the end of the conversation instead of its start.</param>
    /// <param name="find">A word to look for straight away — the one a search found the conversation by.</param>
    public static void ShowFor(Window anchor, SessionRowViewModel row, string transcriptPath, bool jumpToLatest = false,
        string? find = null)
    {
        if (OpenWindows.TryGetValue(row.Metadata.Key, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            if (jumpToLatest) _ = existing.JumpToLatestAsync();
            else if (!string.IsNullOrWhiteSpace(find)) _ = existing.FindTermAsync(find);
            return;
        }

        var window = new TranscriptWindow(row, transcriptPath) { _jumpToLatest = jumpToLatest, _initialFind = find };
        window.PlaceOver(anchor, cascade: OpenWindows.Count);
        OpenWindows[row.Metadata.Key] = window;
        window.Closed += (_, _) => OpenWindows.Remove(row.Metadata.Key);
        window.Show();
    }

    /// <summary>
    /// Centred on the list's window, each further transcript stepped down and right so none
    /// hides another exactly. A spot off every screen (the list on a monitor since unplugged)
    /// falls back to the middle of the main screen once the window exists.
    /// </summary>
    private void PlaceOver(Window anchor, int cascade)
    {
        var bounds = anchor.WindowState == WindowState.Normal
            ? new Rect(anchor.Left, anchor.Top, anchor.ActualWidth, anchor.ActualHeight)
            : anchor.RestoreBounds;
        var step = 28 * (cascade % 6);
        Left = bounds.Left + Math.Max(0, (bounds.Width - Width) / 2) + step;
        Top = bounds.Top + Math.Max(0, (bounds.Height - Height) / 2) + step;
        WindowPlacementMemory.KeepOnScreen(this);
    }

    private bool _jumpToLatest;
    private string? _initialFind;

    private TranscriptWindow(SessionRowViewModel row, string transcriptPath)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        WindowPlacementMemory.Restore(this, AppSettings.Current.TranscriptWindow, restorePosition: false);

        _row = row;
        _path = transcriptPath;
        _reader = new TranscriptReader(transcriptPath, keepImages: true);

        _view = CollectionViewSource.GetDefaultView(_messages);
        _view.Filter = item => DialogueOnly.IsChecked != true
                               || ((MessageViewModel)item) is { IsDialogue: true } or { IsAttachment: true };
        MessageList.ItemsSource = _view;

        Title = $"{row.Title} · 对话记录";
        TitleLine.Text = row.Title;
        TitleLine.ToolTip = row.Title;
        MetaRun.Text = $"{row.Backend}　·　{row.Project}　·　最后活动 {row.LastActivityFull}"
            + $"　·　{_reader.SizeBytes / 1048576.0:N1} MB";
        RevealLink.ToolTip = transcriptPath;
        if (row.CanOpenInApp)
        {
            OpenInAppRun.Text = row.OpenInAppShortLabel;
            OpenInAppLink.ToolTip = row.OpenInAppTooltip + "\n（Ctrl+O）";
        }
        else
        {
            MetaLine.Inlines.Remove(OpenInAppGap);
            MetaLine.Inlines.Remove(OpenInAppLink);
        }

        DialogueOnly.IsChecked = AppSettings.Current.TranscriptDialogueOnly;
        ExpandTools.IsChecked = AppSettings.Current.TranscriptExpandTools;
        _zoom = AppSettings.Current.TranscriptZoom;
        ApplyZoom();

        _findDelay.Tick += (_, _) =>
        {
            _findDelay.Stop();
            RunFind();
        };

        Loaded += async (_, _) =>
        {
            _ = ShowContinuationAsync();
            _scroll = FindDescendant<ScrollViewer>(MessageList);
            if (_scroll is not null) _scroll.ScrollChanged += OnScrollChanged;
            if (_jumpToLatest)
            {
                // Straight to the end: the start would only flash past on the way there.
                await JumpToLatestAsync();
                return;
            }
            await LoadPageAsync(PageSize);
            if (!string.IsNullOrWhiteSpace(_initialFind)) await FindTermAsync(_initialFind);
        };
        Closing += (_, _) => AppSettings.Update(s => s with { TranscriptWindow = WindowPlacementMemory.Capture(this) });
        Closed += (_, _) =>
        {
            _closing.Cancel();
            _bulk?.Cancel();
            // A read still running owns the reader until it notices the cancellation.
            if (_pendingRead is { IsCompleted: false } pending)
                pending.ContinueWith(_ => _reader.Dispose(), TaskScheduler.Default);
            else
                _reader.Dispose();
        };
    }

    // ---------------------------------------------------------------- reading

    /// <summary>
    /// Reads the next page off the UI thread and appends it. Returns false at the end, on
    /// cancellation, or when a page is already being loaded — the window stays responsive the
    /// whole time, however large the file.
    ///
    /// <para>"Being loaded" covers appending the page too, not just reading it: the next page
    /// may only start from the offset this one leaves behind, or it would read the same lines
    /// twice.</para>
    /// </summary>
    private Task<bool> LoadPageAsync(int size)
    {
        if (_reachedEnd || _loading is { IsCompleted: false }) return Task.FromResult(false);
        _loading = LoadPageCoreAsync(size);
        return _loading;
    }

    private async Task<bool> LoadPageCoreAsync(int size)
    {
        var offset = _offset;
        var cancel = _closing.Token;
        var read = Task.Run(() => _reader.Read(offset, size, cancel), cancel);
        _pendingRead = read;
        if (_messages.Count == 0) Placeholder.Text = "正在读取对话…";

        try
        {
            var page = await read;
            var expand = ExpandTools.IsChecked == true;
            var added = new List<MessageViewModel>(page.Entries.Count);
            foreach (var entry in page.Entries)
            {
                var message = new MessageViewModel(entry, _row.AssistantName, expand);
                _messages.Add(message);
                added.Add(message);
            }

            _offset = page.NextLineOffset;
            _toolOutputs = page.ToolOutputsFolded;
            _reachedEnd = page.ReachedEnd;
            _bytePosition = page.BytePosition;

            if (_findTerm.Length > 0) AddMatches(added);
            UpdatePlaceholder();
            if (_bulk is null)
            {
                ShowProgress();
                // A page that added nothing visible ("只看对话" over a run of tool calls) does
                // not move the scroll bar, so no scroll would ever ask for the next one.
                _ = Dispatcher.BeginInvoke(FillViewport, DispatcherPriority.Background);
            }
            return added.Count > 0;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _reachedEnd = true;     // stop the scroll-triggered retries; the message says why
            ShowStatus("读取失败：" + ex.Message, StatusTone.Error);
            UpdatePlaceholder();
            return false;
        }
    }

    private void UpdatePlaceholder()
    {
        var visible = !((ICollectionView?)_tailView ?? _view).IsEmpty;
        Placeholder.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        if (visible) return;
        Placeholder.Text = !_reader.Exists
            ? "transcript 文件不存在：它可能已被清理或移动。"
            : _messages.Count == 0
                ? (_reachedEnd ? "这份记录里没有可显示的对话。" : "正在读取对话…")
                : "这一段只有工具调用和系统提示。取消勾选「只看对话」可以看到它们。";
    }

    /// <summary>What is on screen and what is not yet: a reader should never wonder whether the conversation is complete.</summary>
    private void ShowProgress()
    {
        var dialogue = _messages.Count(m => m.IsDialogue);
        var text = $"已加载 {_messages.Count} 条（对话 {dialogue} 条 · 工具输出 {_toolOutputs} 条已折叠）";
        if (_reachedEnd)
            text += "　·　已到结尾";
        else
        {
            var size = Math.Max(1, _reader.SizeBytes);
            text += $"　·　已读到文件的 {Math.Min(99, _bytePosition * 100 / size)}%，滚动到底部会继续加载";
        }
        ShowStatus(text, StatusTone.Info);
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // ScrollChanged bubbles, and every message is a text box with a scroll viewer inside:
        // without this check each one being laid out would read as "reached the bottom" and
        // pull in the next page, until the whole file was loaded.
        if (!ReferenceEquals(e.OriginalSource, _scroll)) return;
        if (_reachedEnd || _bulk is not null || e.ViewportHeight <= 0) return;
        if (e.VerticalOffset + e.ViewportHeight * 2.5 >= e.ExtentHeight) _ = LoadPageAsync(PageSize);
    }

    private void FillViewport()
    {
        if (_reachedEnd || _bulk is not null || _scroll is not { ViewportHeight: > 0 } scroll) return;
        if (scroll.ExtentHeight <= scroll.ViewportHeight * 1.5) _ = LoadPageAsync(PageSize);
    }

    /// <summary>The reading-on in progress, if any.</summary>
    private Task<bool>? _readingOn;

    /// <summary>
    /// Reads on until <paramref name="until"/> says stop or the file ends, one large page at a
    /// time with the window live in between. Esc or "停止" ends it early. Asked while a read is
    /// already running, it waits for that one instead of returning as if the reading were done.
    /// True unless the reading was stopped.
    /// </summary>
    private Task<bool> ReadOnAsync(string activity, Func<bool>? until = null)
    {
        if (_readingOn is { IsCompleted: false } running) return running;
        _readingOn = ReadOnCoreAsync(activity, until);
        return _readingOn;
    }

    private async Task<bool> ReadOnCoreAsync(string activity, Func<bool>? until)
    {
        // Copying or exporting reads the file under the same stop button: it finishes first.
        while (_bulk is not null)
        {
            await Task.Delay(100);
            if (_closing.IsCancellationRequested) return false;
        }
        _bulk = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        StopButton.Visibility = _tailView is null ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            while (!_reachedEnd && !_bulk.IsCancellationRequested)
            {
                if (_loading is { IsCompleted: false } running) await running;
                var size = Math.Max(1, _reader.SizeBytes);
                ShowStatus($"{activity}… 已读到文件的 {Math.Min(99, _bytePosition * 100 / size)}%", StatusTone.Busy);
                await LoadPageAsync(BulkPageSize);
                if (until?.Invoke() == true) break;
            }
            return !_bulk.IsCancellationRequested;
        }
        finally
        {
            _bulk.Dispose();
            _bulk = null;
            StopButton.Visibility = Visibility.Collapsed;
            ShowProgress();
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _bulk?.Cancel();

    private void Picture_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MessageViewModel { Entry.Image: { } bytes } message })
            Pictures.Show(this, bytes, $"{_row.Title} · 图片 {message.Timestamp}");
    }

    private async void Latest_Click(object sender, RoutedEventArgs e) => await JumpToLatestAsync();

    private void Top_Click(object sender, RoutedEventArgs e) => _scroll?.ScrollToHome();

    /// <summary>Counts finds, so a jump to the end that a find overtook leaves the find's place alone.</summary>
    private int _finds;

    private async Task JumpToLatestAsync()
    {
        if (!_reachedEnd && _bulk is null && _tailView is null && _reader.SizeBytes > TailFirstBytes)
            await ShowTailFirstAsync();
        var finds = _finds;
        // A read already running (a find reading on) may stop short of the end: read on after it.
        while (!_reachedEnd)
            if (!await ReadOnAsync("正在读到结尾")) break;      // stopped by the user
        EndTailPreview();
        if (finds == _finds) ScrollToEndSettled();
    }

    /// <summary>The end, read from the end of the file, shown while the whole is read; null when not showing it.</summary>
    private ListCollectionView? _tailView;

    /// <summary>
    /// Shows the last entries at once, read from the end of the file. The whole conversation is
    /// read behind them and takes their place when it is ready, at the same spot — so 「看最新」
    /// on a conversation of hundreds of megabytes is instant, and nothing is lost for it.
    /// </summary>
    private async Task ShowTailFirstAsync()
    {
        TranscriptTail tail;
        try
        {
            tail = await Task.Run(() => TranscriptReader.ReadTail(_path, TailEntries, keepImages: true, _closing.Token));
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            return;     // the whole read that follows says what went wrong, if anything did
        }
        if (tail.Entries.Count == 0 || _reachedEnd || tail.WholeFile) return;

        var expand = ExpandTools.IsChecked == true;
        var preview = tail.Entries.Select(e => new MessageViewModel(e, _row.AssistantName, expand)).ToList();
        _tailView = new ListCollectionView(preview) { Filter = _view.Filter };
        MessageList.ItemsSource = _tailView;
        Placeholder.Visibility = _tailView.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        TailBanner.Visibility = Visibility.Visible;
        ScrollToEndSettled();
    }

    /// <summary>The whole conversation takes the place of the end shown ahead of it.</summary>
    private void EndTailPreview()
    {
        if (_tailView is null) return;
        var chosen = _tailView.SourceCollection.OfType<MessageViewModel>().Where(m => m.IsSelected)
            .Select(m => (m.Entry.Index, m.Entry.Role, m.Text)).ToHashSet();
        _tailView = null;
        TailBanner.Visibility = Visibility.Collapsed;
        MessageList.ItemsSource = _view;
        if (chosen.Count > 0)
            foreach (var message in _messages.Where(m => chosen.Contains((m.Entry.Index, m.Entry.Role, m.Text)))) message.IsSelected = true;
        ShowSelection(quiet: true);
        UpdatePlaceholder();
        // Still reading behind it: now that the list on screen is the one being read, it can be stopped.
        if (_bulk is not null) StopButton.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// To the very end, and again once the last messages are laid out: a rendered message is
    /// only as tall as its layout says once it is on screen, so one jump can stop short.
    /// </summary>
    private void ScrollToEndSettled(int passes = 6)
    {
        if (MessageList.Items.Count == 0 || _scroll is not { } scroll) return;
        MessageList.ScrollIntoView(MessageList.Items[^1]);
        scroll.ScrollToEnd();
        if (passes <= 0) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (scroll.VerticalOffset < scroll.ScrollableHeight - 1) ScrollToEndSettled(passes - 1);
        }, DispatcherPriority.ContextIdle);
    }

    // ------------------------------------------------------------- display

    private void DialogueOnly_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _view.Refresh();
        _tailView?.Refresh();
        UpdatePlaceholder();
        AppSettings.Update(s => s with { TranscriptDialogueOnly = DialogueOnly.IsChecked == true });
        if (_findTerm.Length > 0) RunFind();
        // Hiding the tool lines can leave too little to scroll; keep reading so the view fills.
        Dispatcher.BeginInvoke(FillViewport, DispatcherPriority.Background);
    }

    private void ExpandTools_Changed(object sender, RoutedEventArgs e)
    {
        var expand = ExpandTools.IsChecked == true;
        foreach (var m in _messages)
            if (m.IsTool) m.IsExpanded = expand;
        if (_tailView is not null)
            foreach (MessageViewModel m in _tailView.SourceCollection)
                if (m.IsTool) m.IsExpanded = expand;
        if (!IsLoaded) return;
        AppSettings.Update(s => s with { TranscriptExpandTools = expand });
        if (_findTerm.Length > 0) RunFind();     // tool text joins or leaves the search with it
    }

    // ---------------------------------------------------------------- find

    private void FindBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var empty = FindBox.Text.Length == 0;
        FindHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ClearFindButton.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        _findDelay.Stop();
        _findDelay.Start();
    }

    private void FindBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _findDelay.Stop();
            if (FindBox.Text.Trim() != _findTerm) RunFind();
            else _ = FindNextAsync(forward: Keyboard.Modifiers != ModifierKeys.Shift);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && FindBox.Text.Length > 0)
        {
            FindBox.Text = string.Empty;
            e.Handled = true;
        }
    }

    private void ClearFind_Click(object sender, RoutedEventArgs e)
    {
        FindBox.Text = string.Empty;
        FindBox.Focus();
    }

    private async void FindNext_Click(object sender, RoutedEventArgs e) => await FindNextAsync(forward: true);
    private async void FindPrevious_Click(object sender, RoutedEventArgs e) => await FindNextAsync(forward: false);

    /// <summary>
    /// Puts a word in the find box and shows its first occurrence, reading on as far as it
    /// takes — the conversation was listed because the word is in it somewhere.
    /// </summary>
    private async Task FindTermAsync(string term)
    {
        FindBox.Text = term.Trim();
        _findDelay.Stop();
        RunFind();
        if (_matches.Count == 0) await FindNextAsync(forward: true);
    }

    /// <summary>Marks every visible entry that contains the term and jumps to the first.</summary>
    private void RunFind()
    {
        // A find works on the conversation as read so far, not on the end shown ahead of it.
        EndTailPreview();
        foreach (var m in _matches)
        {
            m.IsMatch = false;
            m.IsCurrentMatch = false;
            m.HighlightTerm = null;
        }
        _matches.Clear();
        _currentMatch = -1;
        _place = _places = 0;
        _findTerm = FindBox.Text.Trim();
        if (_findTerm.Length > 0) _finds++;

        if (_findTerm.Length == 0)
        {
            MatchCount.Text = string.Empty;
            ShowProgress();
            return;
        }

        AddMatches(_view.Cast<MessageViewModel>());
        if (_matches.Count > 0) MoveTo(0);
        else UpdateMatchCount();
    }

    private void AddMatches(IEnumerable<MessageViewModel> candidates)
    {
        foreach (var m in candidates)
        {
            if (!_view.Filter(m) || !m.Matches(_findTerm, ExpandTools.IsChecked == true)) continue;
            m.IsMatch = true;
            if (m.IsDialogue) m.HighlightTerm = _findTerm;
            _matches.Add(m);
        }
        UpdateMatchCount();
    }

    private void UpdateMatchCount()
    {
        MatchCount.Text = _findTerm.Length == 0 ? string.Empty
            : _matches.Count == 0 ? (_reachedEnd ? "无匹配" : "暂无匹配")
            : _currentMatch < 0 ? $"{_matches.Count} 条"
            : $"{_currentMatch + 1} / {_matches.Count}{(_reachedEnd ? "" : "+")} 条"
              + (_places > 1 ? $"　本条 {_place + 1}/{_places} 处" : "");
        MatchCount.ToolTip = _findTerm.Length == 0 || _matches.Count == 0 ? null
            : "第几条 / 共几条提到它的消息" + (_places > 1 ? "；这一条里提到了多处，F3 逐处跳，跳完再到下一条" : "")
              + (_reachedEnd ? "" : "\n只统计了已加载的部分；按 F3 到最后一处之后会继续读后面的内容");
    }

    /// <summary>
    /// Next or previous match. Past the last loaded one it reads on — a search that silently
    /// stopped at the loaded part would claim "no more" about text it never looked at.
    /// </summary>
    private async Task FindNextAsync(bool forward)
    {
        if (_findTerm.Length == 0)
        {
            FindBox.Focus();
            return;
        }

        // A long message says it in several places: each of them before the next message.
        if (_currentMatch >= 0 && _places > 1)
        {
            var place = _place + (forward ? 1 : -1);
            if (place >= 0 && place < _places)
            {
                _place = place;
                var current = _matches[_currentMatch];
                MessageList.ScrollIntoView(current);
                _ = Dispatcher.BeginInvoke(() => SettleInView(current), DispatcherPriority.ContextIdle);
                UpdateMatchCount();
                return;
            }
        }

        var next = _currentMatch + (forward ? 1 : -1);
        if (next >= 0 && next < _matches.Count)
        {
            MoveTo(next, lastPlace: !forward);
            return;
        }

        if (forward && !_reachedEnd)
        {
            var before = _matches.Count;
            var read = true;
            // A read already running is waited for; if it stopped short for its own reason, read on.
            while (read && _matches.Count == before && !_reachedEnd)
                read = await ReadOnAsync("正在后面的内容里查找", until: () => _matches.Count > before);
            if (_matches.Count > before)
            {
                MoveTo(before);
                return;
            }
            if (!read)
            {
                ShowStatus($"读取已停止：已读的部分里没有更多「{_findTerm}」", StatusTone.Info);
                return;
            }
        }

        if (_matches.Count == 0)
        {
            ShowStatus($"整条对话里都没有「{_findTerm}」", StatusTone.Info);
            return;
        }

        MoveTo(forward ? 0 : _matches.Count - 1, lastPlace: !forward);
        ShowStatus(forward ? "已到最后一处，从头继续" : "已到第一处，从末尾继续", StatusTone.Info);
    }

    /// <param name="lastPlace">Coming back to it: its last place rather than its first.</param>
    private void MoveTo(int index, bool lastPlace = false)
    {
        if (_currentMatch >= 0 && _currentMatch < _matches.Count)
        {
            var previous = _matches[_currentMatch];
            previous.IsCurrentMatch = false;
            MarkPlaces(previous, current: -1);
        }
        _currentMatch = index;
        _place = lastPlace ? int.MaxValue : 0;
        _places = 0;
        var match = _matches[index];
        match.IsCurrentMatch = true;
        // A hit inside folded tool text is no use folded.
        if (match.HasDetail && !match.IsExpanded && !match.TextContains(_findTerm)) match.IsExpanded = true;
        MessageList.ScrollIntoView(match);
        // ScrollIntoView finishes at Loaded priority when it has to realise the item first.
        Dispatcher.BeginInvoke(() => SettleInView(match), DispatcherPriority.ContextIdle);
        UpdateMatchCount();
    }

    /// <summary>
    /// ScrollIntoView stops as soon as the item is visible, which leaves a match pinned to the
    /// window's edge with nothing after it. It goes a third of the way down instead, so what
    /// was said around it — the reason for searching — is on screen too.
    /// </summary>
    private void SettleInView(MessageViewModel match)
    {
        if (!match.IsCurrentMatch || _scroll is not { ViewportHeight: > 0 } scroll) return;
        if (MessageList.ItemContainerGenerator.ContainerFromItem(match) is not FrameworkElement container) return;

        // The places the word is marked in this message, the current one in a colour of its own.
        var (box, places) = MarkPlaces(match, _place);
        _places = places.Count;
        _place = Math.Clamp(_place, 0, Math.Max(0, places.Count - 1));
        UpdateMatchCount();

        var top = container.TransformToAncestor(scroll).Transform(new Point(0, 0)).Y;
        if (container.ActualHeight > scroll.ViewportHeight * 0.6)
        {
            // A long message: the place itself goes a third of the way down; without one the
            // message starts at the top so its beginning shows.
            var at = box is not null && places.Count > 0 ? PlaceOffset(box, places[_place], container) : null;
            scroll.ScrollToVerticalOffset(Math.Max(0, scroll.VerticalOffset + top
                + (at is { } y ? y - scroll.ViewportHeight / 3 : -8)));
            return;
        }
        scroll.ScrollToVerticalOffset(Math.Max(0, scroll.VerticalOffset + top - (scroll.ViewportHeight - container.ActualHeight) / 3));
    }

    /// <summary>
    /// Colours the places the word is marked in a message shown on screen: the one at
    /// <paramref name="current"/> apart from the rest (-1: none). Nothing for a message off screen.
    /// </summary>
    private (RichTextBox? Box, List<System.Windows.Documents.Run> Places) MarkPlaces(MessageViewModel message, int current)
    {
        if (MessageList.ItemContainerGenerator.ContainerFromItem(message) is not FrameworkElement container
            || FindDescendant<RichTextBox>(container) is not { } box)
            return (null, []);
        var places = MarkdownDocument.Highlights(box.Document);
        var at = Math.Min(current, places.Count - 1);
        for (var i = 0; i < places.Count; i++)
        {
            places[i].SetResourceReference(System.Windows.Documents.TextElement.BackgroundProperty,
                i == at ? "FindCurrentBrush" : "FindHighlightBrush");
            if (i == at) places[i].SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "FindCurrentTextBrush");
            else places[i].ClearValue(System.Windows.Documents.TextElement.ForegroundProperty);
        }
        return (box, places);
    }

    /// <summary>How far down a message's container a marked place sits; null before it is laid out.</summary>
    private static double? PlaceOffset(RichTextBox box, System.Windows.Documents.Run place, FrameworkElement container)
    {
        var rect = place.ContentStart.GetCharacterRect(System.Windows.Documents.LogicalDirection.Forward);
        if (rect.IsEmpty) return null;
        return box.TransformToAncestor(container).Transform(rect.TopLeft).Y;
    }

    private void Changes_Click(object sender, RoutedEventArgs e) => ChangesWindow.ShowFor(this, _row, _path);

    // ----------------------------------------------------------------- zoom

    private double _zoom = 1;

    /// <summary>
    /// Text size for the conversation, like a browser's zoom: for a high-resolution screen, a
    /// long read, or eyes that want it larger. Only the text grows; the window stays laid out.
    /// </summary>
    private void Zoom(double factor)
    {
        var next = factor == 0 ? 1 : Math.Round(Math.Clamp(_zoom + factor, 0.7, 2), 2);
        if (Math.Abs(next - _zoom) < 0.001) return;
        _zoom = next;
        ApplyZoom();
        AppSettings.Update(s => s with { TranscriptZoom = _zoom });
        ShowStatus($"字号 {_zoom:P0}" + (Math.Abs(_zoom - 1) < 0.001 ? "" : "（Ctrl+0 还原）"), StatusTone.Info);
    }

    private void ApplyZoom()
    {
        Resources["MessageFontSize"] = 13.5 * _zoom;
        Resources["MessageLineHeight"] = 13.5 * 1.6 * _zoom;
        Resources["MessageH1FontSize"] = 13.5 * 1.45 * _zoom;
        Resources["MessageH2FontSize"] = 13.5 * 1.3 * _zoom;
        Resources["MessageH3FontSize"] = 13.5 * 1.15 * _zoom;
        Resources["LineFontSize"] = 12 * _zoom;
        Resources["DetailFontSize"] = 12 * _zoom;
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            Zoom(e.Delta > 0 ? 0.1 : -0.1);
            e.Handled = true;
        }
        base.OnPreviewMouseWheel(e);
    }

    // ------------------------------------------------------------- keyboard

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var inReadOnlyText = Keyboard.FocusedElement is TextBox { IsReadOnly: true };
        switch (e.Key)
        {
            case Key.F when mods == ModifierKeys.Control:
                FindBox.Focus();
                FindBox.SelectAll();
                e.Handled = true;
                break;
            case Key.O when mods == ModifierKeys.Control:
                _ = OpenInAppAsync();
                e.Handled = true;
                break;
            case Key.F3:
                _ = FindNextAsync(forward: mods != ModifierKeys.Shift);
                e.Handled = true;
                break;
            case Key.OemPlus or Key.Add when mods == ModifierKeys.Control:
                Zoom(0.1);
                e.Handled = true;
                break;
            case Key.OemMinus or Key.Subtract when mods == ModifierKeys.Control:
                Zoom(-0.1);
                e.Handled = true;
                break;
            case Key.D0 or Key.NumPad0 when mods == ModifierKeys.Control:
                Zoom(0);
                e.Handled = true;
                break;
            case Key.End when mods == ModifierKeys.Control:
                _ = JumpToLatestAsync();
                e.Handled = true;
                break;
            case Key.Home when mods == ModifierKeys.Control:
                _scroll?.ScrollToHome();
                e.Handled = true;
                break;
            // A message the user clicked into would otherwise keep these keys to itself.
            case Key.PageDown when inReadOnlyText:
                _scroll?.PageDown();
                e.Handled = true;
                break;
            case Key.PageUp when inReadOnlyText:
                _scroll?.PageUp();
                e.Handled = true;
                break;
            case Key.Escape when (!FindBox.IsKeyboardFocused || FindBox.Text.Length == 0) && SelectedMessages().Count > 0:
                ClearSelection();
                e.Handled = true;
                break;
            case Key.C when mods == ModifierKeys.Control && !HasTextSelection() && SelectedMessages().Count > 0:
                CopySelected();
                e.Handled = true;
                break;
            case Key.Escape when !FindBox.IsKeyboardFocused || FindBox.Text.Length == 0:
                // While the end is shown ahead of the whole, stopping would leave half a conversation
                // standing in for the end; Esc closes instead, as it does when nothing is being read.
                if (_bulk is not null && _tailView is null) _bulk.Cancel();
                else Close();
                e.Handled = true;
                break;
        }
        base.OnPreviewKeyDown(e);
    }

    // --------------------------------------------------------- copy & export

    /// <summary>
    /// The whole transcript for copying or exporting — never just the pages on screen. When
    /// the view has already read to the end it is reused; otherwise the file is read once
    /// more, off the UI thread, with progress, and Esc or "停止" can end it.
    /// </summary>
    /// <param name="pictures">Keep the pictures: for a page that shows them.</param>
    private async Task<TranscriptContent?> ReadEverythingAsync(string activity, bool pictures = false)
    {
        if (_reachedEnd) return new TranscriptContent(_messages.Select(m => m.Entry).ToList(), _toolOutputs);
        if (_bulk is not null)
        {
            ShowStatus($"正在读取对话，读完后再{activity}", StatusTone.Info);
            return null;
        }

        _bulk = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        var token = _bulk.Token;
        StopButton.Visibility = Visibility.Visible;
        CopyButton.IsEnabled = ExportButton.IsEnabled = false;
        var progress = new Progress<double>(p => ShowStatus($"{activity}：正在读取全文… {p:P0}", StatusTone.Busy));
        try
        {
            return await Task.Run(() => TranscriptExport.ReadAll(_path, progress, token, keepImages: pictures), token);
        }
        catch (OperationCanceledException)
        {
            ShowStatus($"已停止，没有{activity}", StatusTone.Info);
            return null;
        }
        catch (Exception ex)
        {
            ShowStatus($"{activity}失败：{ex.Message}", StatusTone.Error);
            return null;
        }
        finally
        {
            _bulk.Dispose();
            _bulk = null;
            StopButton.Visibility = Visibility.Collapsed;
            CopyButton.IsEnabled = ExportButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// The copy button on one message: its whole text — the assistant's Markdown as written, and
    /// all of a message too long to lay out, not the part the bubble shows.
    /// </summary>
    private void CopyMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MessageViewModel message }) return;
        if (Shell.Copy(message.Text) is { } error)
        {
            ShowStatus("复制失败：" + error, StatusTone.Error);
            return;
        }
        var which = message.Entry.Role == MessageRole.User ? "你说的这条" : message.Speaker + " 的这条回复";
        ShowStatus($"已复制{which}：{message.Text.Length:N0} 字", StatusTone.Success);
        message.JustCopied = true;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            message.JustCopied = false;
        };
        timer.Start();
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (await ReadEverythingAsync("复制") is not { } content) return;

        var text = TranscriptExport.ToPlainText(content.Entries, _row.AssistantName);
        if (text.Length == 0)
        {
            ShowStatus("这条对话里没有可复制的发言", StatusTone.Info);
            return;
        }
        var error = Shell.Copy(text);
        var turns = content.Entries.Count(entry => entry.Role is MessageRole.User or MessageRole.Assistant);
        ShowStatus(error is null ? $"已复制整条对话：{turns} 条发言，{text.Length:N0} 字" : "复制失败：" + error,
            error is null ? StatusTone.Success : StatusTone.Error);
    }

    private async void Export_Click(object sender, RoutedEventArgs e) => await ExportAsync(null);

    /// <summary>The whole conversation, read to the end; or, given <paramref name="excerpt"/>, just those messages.</summary>
    private async Task ExportAsync(IReadOnlyList<MessageViewModel>? excerpt)
    {
        // Opens in this tool's own export folder rather than wherever Windows last left the
        // dialog, so saved conversations collect in one place on the data drive.
        Directory.CreateDirectory(LauncherPaths.ExportDirectory);
        var lastWasPage = AppSettings.Current.ExportFormat == "html";
        var part = excerpt is null ? "" : $"（节选 {excerpt.Count} 条）";
        var dialog = new SaveFileDialog
        {
            FileName = TranscriptExport.SafeFileName(_row.Title) + part + (lastWasPage ? ".html" : ".md"),
            Filter = "Markdown (*.md)|*.md|网页，含图片，双击即可查看 (*.html)|*.html|文本 (*.txt)|*.txt",
            FilterIndex = lastWasPage ? 2 : 1,
            Title = excerpt is null ? "导出对话记录" : $"导出选中的 {excerpt.Count} 条发言",
            InitialDirectory = LauncherPaths.ExportDirectory,
        };
        if (dialog.ShowDialog(this) != true) return;

        // The kind chosen in the dialog decides — or a name typed ending in .html. A page named
        // otherwise is given .html, and a file already there under that name is not replaced unasked:
        // the dialog only asked about the name as typed.
        var file = dialog.FileName;
        var extension = Path.GetExtension(file).ToLowerInvariant();
        var asPage = dialog.FilterIndex == 2 || extension is ".html" or ".htm";
        if (asPage && extension is not (".html" or ".htm"))
        {
            file = Path.ChangeExtension(file, ".html");
            if (File.Exists(file) && MessageDialog.Show(this, DialogKind.Question, "替换已有的文件？",
                    $"网页要以 .html 结尾，所以会存成「{Path.GetFileName(file)}」，而这个文件已经存在。要替换它吗？",
                    "替换", cancel: "取消") != DialogChoice.Primary)
                return;
        }
        AppSettings.Update(s => s with { ExportFormat = asPage ? "html" : "md" });

        var content = excerpt is null
            ? await ReadEverythingAsync("导出", pictures: asPage)
            : new TranscriptContent(excerpt.Select(m => m.Entry).ToList(), 0);
        if (content is null) return;

        try
        {
            var header = new TranscriptHeader(_row.Title + part, _row.Backend, _row.ProjectRef, _row.SessionId, _row.LastActivityFull);
            var tools = ExpandTools.IsChecked == true;
            var pictures = 0;
            if (asPage)
            {
                var assistant = _row.AssistantName;
                pictures = await Task.Run(() =>
                {
                    using var page = new StreamWriter(file, append: false, new UTF8Encoding(false));
                    return TranscriptExport.WriteHtml(page, header, content, assistant, tools, DateTime.Now);
                });
            }
            else
            {
                var markdown = TranscriptExport.ToMarkdown(header, content, _row.AssistantName, tools);
                await File.WriteAllTextAsync(file, markdown, new UTF8Encoding(true));
            }
            ShowStatus((excerpt is null ? $"已导出整条对话（{content.Entries.Count} 条" : $"已导出选中的 {excerpt.Count} 条发言（")
                       + $"{(pictures > 0 ? $"，{pictures} 张图片" : "")}）：{file}",
                StatusTone.Success, asPage ? "打开" : "在文件夹中显示", () =>
                {
                    if ((asPage ? Shell.Open(file) : Shell.Reveal(file)) is { } error) ShowStatus("打开失败：" + error, StatusTone.Error);
                });
        }
        catch (Exception ex)
        {
            ShowStatus("导出失败：" + ex.Message, StatusTone.Error);
        }
    }

    // ------------------------------------------------------------- choosing messages

    private MessageViewModel? _selectionAnchor;

    /// <summary>The messages on screen: the whole conversation, or the end shown ahead of it.</summary>
    private IEnumerable<MessageViewModel> ShownMessages() =>
        _tailView?.SourceCollection.OfType<MessageViewModel>() ?? _messages;

    /// <summary>The chosen messages, in the order the conversation has them.</summary>
    private List<MessageViewModel> SelectedMessages() => [.. ShownMessages().Where(m => m.IsSelected)];

    /// <summary>
    /// The tick box on a message: chooses it, or lets it go. With Shift, every message from the
    /// one chosen last up to this one is chosen.
    /// </summary>
    private void SelectMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MessageViewModel message }) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _selectionAnchor is { } anchor && anchor != message)
        {
            var said = ShownMessages().Where(m => m.IsDialogue).ToList();
            int from = said.IndexOf(anchor), to = said.IndexOf(message);
            if (from >= 0 && to >= 0)
            {
                foreach (var m in said.GetRange(Math.Min(from, to), Math.Abs(from - to) + 1)) m.IsSelected = true;
                _selectionAnchor = message;
                ShowSelection();
                return;
            }
        }
        message.IsSelected = !message.IsSelected;
        _selectionAnchor = message;
        ShowSelection();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var message in ShownMessages().Where(m => m.IsDialogue)) message.IsSelected = true;
        ShowSelection();
        if (!_reachedEnd) ShowStatus(StatusRun.Text + "　·　对话还没读完，只选了已显示的部分", StatusTone.Info);
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e) => ClearSelection();

    private void ClearSelection()
    {
        foreach (var message in ShownMessages().Where(m => m.IsSelected)) message.IsSelected = false;
        ShowSelection();
    }

    private void CopySelected_Click(object sender, RoutedEventArgs e) => CopySelected();

    private void CopySelected()
    {
        var chosen = SelectedMessages();
        if (chosen.Count == 0) return;
        var text = TranscriptExport.ToPlainText(chosen.Select(m => m.Entry), _row.AssistantName);
        var error = Shell.Copy(text);
        ShowStatus(error is null ? $"已复制选中的 {chosen.Count} 条发言：{text.Length:N0} 字" : "复制失败：" + error,
            error is null ? StatusTone.Success : StatusTone.Error);
    }

    private async void ExportSelected_Click(object sender, RoutedEventArgs e)
    {
        var chosen = SelectedMessages();
        if (chosen.Count > 0) await ExportAsync(chosen);
    }

    /// <summary>Text selected inside a message: Ctrl+C copies that, not the chosen messages.</summary>
    private static bool HasTextSelection() => Keyboard.FocusedElement switch
    {
        RichTextBox box => !box.Selection.IsEmpty,
        TextBox box => box.SelectionLength > 0,
        _ => false,
    };

    /// <summary>
    /// While messages are chosen, what can be done with them takes the place of the buttons for
    /// the whole conversation, and the status line counts them.
    /// </summary>
    private void ShowSelection(bool quiet = false)
    {
        var chosen = SelectedMessages();
        var any = chosen.Count > 0;
        var wasShown = SelectionButtons.Visibility == Visibility.Visible;
        SelectionButtons.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        CopyButton.Visibility = ExportButton.Visibility = ChangesButton.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        if (!any)
        {
            _selectionAnchor = null;
            if (wasShown && !quiet) ShowStatus("已取消选择", StatusTone.Info);
            return;
        }
        CopySelectedText.Text = $"复制选中的 {chosen.Count} 条";
        if (!quiet)
            ShowStatus($"已选中 {chosen.Count} 条发言，共 {chosen.Sum(m => m.Text.Length):N0} 字　·　按住 Shift 点勾选框可连选一段，Esc 取消",
                StatusTone.Info);
    }

    /// <summary>Set by the list's window: a conversation's other parts — its desktop session chain and its resumes.</summary>
    public static Func<SessionRowViewModel, Task<Relatives>>? RelativesOf { get; set; }

    /// <summary>Set by the list's window: opens another conversation's transcript, placed over this reader.</summary>
    public static Action<Window, SessionMetadata>? OpenOther { get; set; }

    /// <summary>
    /// For a conversation Claude carried on across transcripts, where the rest of it is: the
    /// desktop session's transcript starts where its last earlier part stopped, every part is
    /// one click from the one before it and the one after, a resumed copy from its original,
    /// and a conversation resumed elsewhere from the latest copy.
    /// </summary>
    private async Task ShowContinuationAsync()
    {
        if (RelativesOf is not { } relativesOf) return;
        Relatives relatives;
        try
        {
            relatives = await relativesOf(_row);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException
                                       or Microsoft.Data.Sqlite.SqliteException)
        {
            return;
        }

        var line = ContinuationLine.Inlines;
        line.Clear();
        var chain = relatives.Chain;
        var at = -1;
        for (var i = 0; i < chain.Count; i++)
            if (chain[i].Key == _row.Metadata.Key) at = i;
        if (at >= 0)
        {
            line.Add(new System.Windows.Documents.Run(ContinuationNames.Where(at, chain.Count) + "　"));
            if (at > 0)
                line.Add(LinkTo(chain[at - 1], "打开" + ContinuationNames.Of(at - 1, at, chain.Count)));
            if (at > 0 && at < chain.Count - 1) line.Add(new System.Windows.Documents.Run("　·　"));
            if (at < chain.Count - 1)
                line.Add(LinkTo(chain[at + 1], "打开" + ContinuationNames.Of(at + 1, at, chain.Count)));
        }
        var fork = _row.IsCodex;
        if (relatives.Resumes.From is { } from)
        {
            if (line.Count > 0) line.Add(new System.Windows.Documents.LineBreak());
            line.Add(new System.Windows.Documents.Run(ContinuationNames.ResumedFromWhere(fork) + "　"));
            line.Add(LinkTo(from, fork ? "打开原任务" : "打开原会话"));
        }
        if (relatives.Resumes.Into is { Count: > 0 } into)
        {
            if (line.Count > 0) line.Add(new System.Windows.Documents.LineBreak());
            line.Add(new System.Windows.Documents.Run(ContinuationNames.ResumedIntoWhere(into.Count, fork) + "　"));
            line.Add(LinkTo(into[^1], fork
                ? into.Count == 1 ? "打开分叉的任务" : "打开最近一次分叉的任务"
                : into.Count == 1 ? "打开续接的会话" : "打开最近一次续接的会话"));
        }
        ContinuationBar.Visibility = line.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private System.Windows.Documents.Hyperlink LinkTo(SessionMetadata part, string text)
    {
        var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(text))
        {
            ToolTip = part.DisplayTitle
                      + (part.LastActivityUtc is { } t ? $"\n最后活动 {RelativeTime.Full(t.LocalDateTime)}" : "")
                      + (part.TranscriptBytes is { } bytes ? $"\n{SessionRowViewModel.Size(bytes)}" : ""),
        };
        link.Click += (_, _) => OpenOther?.Invoke(this, part);
        return link;
    }

    /// <summary>Set by the list's window: opens a conversation in its own app, as the list's 「在应用中打开」 does.</summary>
    public static Func<SessionRowViewModel, Task<DeepLinkResult>>? OpenInApp { get; set; }

    private async void OpenInApp_Click(object sender, RoutedEventArgs e) => await OpenInAppAsync();

    private async Task OpenInAppAsync()
    {
        if (!_row.CanOpenInApp || OpenInApp is not { } open) return;
        ShowStatus($"正在请求 {_row.AppName} 打开这条对话…", StatusTone.Busy);
        var result = await open(_row);
        ShowStatus(result.Message, result.Outcome == DeepLinkOutcome.Requested ? StatusTone.Success : StatusTone.Error);
    }

    private void RevealFile_Click(object sender, RoutedEventArgs e)
    {
        if (Shell.Reveal(_path) is { } error) ShowStatus("打开失败：" + error, StatusTone.Error);
    }

    // --------------------------------------------------------------- status

    private void ShowStatus(string text, StatusTone tone, string? actionLabel = null, Action? action = null)
    {
        _statusAction = action;
        StatusRun.Text = text;
        StatusGap.Text = action is null ? string.Empty : "　";
        StatusLinkRun.Text = action is null ? string.Empty : actionLabel ?? string.Empty;
        StatusLink.IsEnabled = action is not null;
        StatusLine.ToolTip = text.Length > 60 ? text : null;
        MainWindow.Announce(StatusLine);
        (StatusGlyph.Text, StatusGlyph.Foreground) = tone switch
        {
            StatusTone.Busy => ("", Palette.Info),
            StatusTone.Success => ("", Palette.Success),
            StatusTone.Warning => ("", Palette.Warn),
            StatusTone.Error => ("", Palette.Error),
            _ => ("", Palette.Faint),
        };
        StatusRun.Foreground = tone == StatusTone.Error ? Palette.Error : Palette.Muted;
    }

    private void StatusLink_Click(object sender, RoutedEventArgs e) => _statusAction?.Invoke();

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
}
