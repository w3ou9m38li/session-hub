using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClaudeLauncher.App.ViewModels;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure;
using ClaudeLauncher.Infrastructure.Catalog;
using ClaudeLauncher.Infrastructure.Launch;
using ClaudeLauncher.Infrastructure.Settings;
using Visibility = System.Windows.Visibility;

namespace ClaudeLauncher.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    /// <summary>Keeps "3 分钟前" true while the window stays open.</summary>
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMinutes(1) };

    /// <summary>
    /// Checks for new and changed conversations without being asked: once a minute while the
    /// window is on screen, and on coming back to it (see <see cref="RefreshIfStale"/>).
    /// </summary>
    private readonly DispatcherTimer _autoSync = new() { Interval = TimeSpan.FromSeconds(60) };

    /// <summary>When the user last moved the pointer, clicked, scrolled or typed in this window.</summary>
    private long _lastInputTicks;

    /// <summary>
    /// Conversations selected just before the list was replaced. A background sync finishing
    /// while the user has rows selected must not silently drop that selection.
    /// </summary>
    private HashSet<ConversationKey>? _reselect;
    private int _reselectIndex = -1;
    private double? _restoreOffset;
    private bool _listHadFocus;

    private ScrollViewer? _listScroll;

    public MainWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);

        var settings = AppSettings.Current;
        WindowPlacementMemory.Restore(this, settings.MainWindow);
        if (settings.MainWindow is null) FitToWorkArea();
        _vm.UseBackendFilter(settings.AppFilter);
        _vm.UseTimeFilter(settings.TimeFilter);
        _vm.UseSort(
            Enum.TryParse<SortField>(settings.SortField, out var savedField) && Enum.IsDefined(savedField)
                ? new SortOrder(savedField, settings.SortReversed) : SortOrder.Default,
            Enum.TryParse<SortField>(settings.SearchSortField, out var searchField) && Enum.IsDefined(searchField)
                ? new SortOrder(searchField, settings.SearchSortReversed) : MainViewModel.RelevanceFirst);

        SessionList.ItemsSource = _vm.Sessions;
        CoverageList.ItemsSource = _vm.CoverageItems;
        GroupFilterBox.ItemsSource = _vm.GroupOptions;
        GroupFilterBox.SelectedItem = _vm.SelectedGroupOption;
        BackendFilterBox.ItemsSource = _vm.BackendOptions;
        BackendFilterBox.SelectedItem = _vm.SelectedBackendOption;
        TimeFilterBox.ItemsSource = _vm.TimeOptions;
        TimeFilterBox.SelectedItem = _vm.SelectedTimeOption;
        CoverageExpander.IsExpanded = settings.CoverageExpanded;
        BuildColumnHeaders();
        ShowSort();
        SessionList.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(ColumnHeader_Click));
        DataRootRun.Text = "数据目录 " + _vm.DataRoot;
        ShowContentSearchChoice();
        WireSearchHistory();
        // A second copy on its own data directory runs beside the everyday one; the title
        // (and so the taskbar) says which is which — and says so too when the directory asked
        // for was refused and this copy is in fact using the everyday one.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(LauncherPaths.DataRootVariable)))
            Title += LauncherPaths.DataRootFromVariable
                ? $"（独立数据目录 {System.IO.Path.GetFileName(_vm.DataRoot.TrimEnd('\\', '/'))}）"
                : "（CLAUDE_LAUNCHER_DATA_DIR 未生效，用的是默认数据目录）";

        _vm.CoverageItems.CollectionChanged += (_, e) =>
        {
            // A new report opens at its top; an automatic refresh of it keeps the reader's place.
            if (e.Action == NotifyCollectionChangedAction.Reset && !_vm.CoverageRefreshIsQuiet) CoverageScroll.ScrollToTop();
        };
        _vm.PropertyChanged += OnViewModelChanged;
        _vm.UsageChanged += () => Dispatcher.BeginInvoke(() => _ = ShowUsageAsync(SingleSelected()));
        TranscriptWindow.OpenInApp = row => _vm.OpenInAppAsync(row);
        TranscriptWindow.RelativesOf = row => _vm.RelativesAsync(row);
        TranscriptWindow.OpenOther = (reader, row) => OpenTranscriptFor(new SessionRowViewModel(row), anchor: reader);
        _vm.ListReplacing += preserve =>
        {
            // Replacing the rows clears the selection for a moment; the panel must not show
            // 「未选择会话」 and drop the preview in between, only to rebuild both a frame later.
            _restoringSelection = true;
            // A conversation asked for from elsewhere (the usage window) is the one to select — for
            // a few seconds: clearing the filters reloads the list before its search does.
            _reselect = _locate is { } wanted && Environment.TickCount64 < _locateUntil
                ? [wanted]
                : Selected().Select(r => r.Metadata.Key).ToHashSet();
            _reselectIndex = SessionList.SelectedIndex;
            _listHadFocus = SessionList.IsKeyboardFocusWithin;
            _restoreOffset = preserve ? _listScroll?.VerticalOffset : null;
            ApplySections();
        };
        _vm.Sessions.CollectionChanged += OnSessionsChanged;

        RestoreColumnWidths();
        ShowStatus(_vm.Status);
        ShowEmpty(_vm.Empty);
        UpdateSelectionUi();

        _clock.Tick += (_, _) =>
        {
            foreach (var row in _vm.Sessions) row.RefreshTime();
            _vm.RefreshIfDayChanged();
        };
        _clock.Start();

        // Any input marks the user as busy for a moment; an automatic refresh waits for that
        // moment to pass so the list never reorders under a pointer about to click.
        PreviewMouseMove += (_, _) => _lastInputTicks = Environment.TickCount64;
        PreviewMouseDown += (_, _) => _lastInputTicks = Environment.TickCount64;
        PreviewMouseWheel += (_, _) => _lastInputTicks = Environment.TickCount64;
        PreviewKeyDown += (_, _) => _lastInputTicks = Environment.TickCount64;
        _vm.IsUserBusy = () =>
            Environment.TickCount64 - _lastInputTicks < 1500
            || Mouse.LeftButton == MouseButtonState.Pressed
            || GroupFilterBox.IsDropDownOpen || BackendFilterBox.IsDropDownOpen || SortBox.IsDropDownOpen
            || SessionList.ContextMenu is { IsOpen: true } || MoreMenu.IsOpen || SearchHistoryPopup.IsOpen;
        Activated += (_, _) => RefreshIfStale(TimeSpan.FromSeconds(15));
        StateChanged += (_, _) => ShowSyncSpin();
        IsVisibleChanged += (_, _) => ShowSyncSpin();
        _autoSync.Tick += (_, _) =>
        {
            RefreshIfStale(TimeSpan.FromSeconds(50));
            // Also while hidden: memory a long transcript or the usage window went through goes back.
            if (MemoryRelief.IsDue && _vm.IsUserBusy?.Invoke() != true) _ = MemoryRelief.ReleaseAsync();
        };
        _autoSync.Start();

        // Closing the window while the tray is on puts it away instead; logging off still ends it.
        Closing += (_, e) =>
        {
            if (_tray is null || _exitRequested) return;
            e.Cancel = true;
            Hide();
            if (_toldAboutTray) return;
            _toldAboutTray = true;
            _tray.Balloon("会话管家还在运行", _hotkey is { } hotkey
                ? $"点托盘图标或按 {hotkey.Name} 回来；右键图标可以退出。"
                : "点托盘图标回来；右键图标可以退出。");
        };
        Application.Current.SessionEnding += (_, _) => _exitRequested = true;

        SourceInitialized += (_, _) =>
            System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle)
                ?.AddHook(WatchWindowsTheme);

        Loaded += async (_, _) =>
        {
            ApplyBackgroundSettings();
            ShowThemeChoice();
            _listScroll = FindDescendant<ScrollViewer>(SessionList);
            if (_listScroll is not null) _listScroll.ScrollChanged += OnListScrollChanged;
            FitTitleColumn();
            SearchBox.Focus();

            if (_vm.RebuildNotice is { } notice)
                MessageDialog.Show(this, DialogKind.Warning, "目录数据库已重建", notice);

            await _vm.InitializeAsync();
        };
        Closing += (_, _) =>
        {
            RememberSearch();
            AppSettings.Update(s => s with { MainWindow = WindowPlacementMemory.Capture(this) });
        };
        Closed += (_, _) =>
        {
            _hotkey?.Dispose();
            _tray?.Dispose();
            _clock.Stop();
            _autoSync.Stop();
            _previewCts?.Cancel();
            _vm.Dispose();
        };
    }

    /// <summary>
    /// A quiet sync when the list may be out of date: not while minimised (coming back
    /// activates the window, which asks again), and not before the first sync has finished.
    /// </summary>
    private void RefreshIfStale(TimeSpan olderThan)
    {
        if (WindowState == WindowState.Minimized || !IsVisible) return;
        if (_vm.LastSyncedUtc is not { } last || DateTime.UtcNow - last < olderThan) return;
        _ = _vm.SyncAsync(quiet: true);
    }

    /// <summary>
    /// The default size suits a large screen; on a smaller one (1080p at 150 % is 1280×720
    /// logical) a window taller than the work area would open with its bottom cut off.
    /// </summary>
    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        if (Height > area.Height) Height = Math.Max(MinHeight, area.Height - 16);
        if (Width > area.Width) Width = Math.Max(MinWidth, area.Width - 16);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.Status):
                ShowStatus(_vm.Status);
                break;
            case nameof(MainViewModel.InstallationText):
                InstallationRun.Text = _vm.InstallationText;
                break;
            case nameof(MainViewModel.DeepSeekLaunchTarget):
                // Shown only where DeepSeek Harness has an entry point of the user's own to start it by.
                OpenDeepSeekButton.Visibility = _vm.DeepSeekLaunchTarget is null ? Visibility.Collapsed : Visibility.Visible;
                if (_vm.DeepSeekLaunchTarget is { } target)
                    OpenDeepSeekButton.ToolTip = $"通过「{DeepSeekHarnessLauncher.NameOf(target)}」启动 DeepSeek Harness，在浏览器里打开；已在运行时直接打开网页\n{target}";
                UpdateSelectionUi();
                break;
            case nameof(MainViewModel.HasMorePages):
                LoadMoreButton.Visibility = _vm.HasMorePages ? Visibility.Visible : Visibility.Collapsed;
                break;
            case nameof(MainViewModel.ListSummary):
                ListSummary.Text = _vm.ListSummary;
                break;
            case nameof(MainViewModel.IsSyncing):
                SetSyncing(_vm.IsSyncing);
                break;
            case nameof(MainViewModel.SearchesContent):
                ShowContentSearchChoice();
                break;
            case nameof(MainViewModel.LastSyncText) or nameof(MainViewModel.IndexProgress):
                LastSyncText.Text = _vm.IndexProgress is { } indexing ? _vm.LastSyncText + "　·　" + indexing : _vm.LastSyncText;
                break;
            case nameof(MainViewModel.Views):
                ActiveCountRun.Text = " " + _vm.Views.Active;
                ArchivedCountRun.Text = " " + _vm.Views.Archived;
                break;
            case nameof(MainViewModel.Empty):
                ShowEmpty(_vm.Empty);
                break;
            case nameof(MainViewModel.CoverageSummary) or nameof(MainViewModel.CoverageSummaryTone):
                CoverageSummaryText.Text = _vm.CoverageSummary;
                CoverageSummaryText.Foreground = _vm.CoverageSummaryTone switch
                {
                    CoverageTone.Ok => Palette.Success,
                    CoverageTone.Warn => Palette.Warn,
                    _ => Palette.Muted,
                };
                break;
            case nameof(MainViewModel.SelectedGroupOption):
                GroupFilterBox.SelectedItem = _vm.SelectedGroupOption;
                break;
            case nameof(MainViewModel.SelectedBackendOption):
                BackendFilterBox.SelectedItem = _vm.SelectedBackendOption;
                AppSettings.Update(s => s with { AppFilter = _vm.SelectedBackendOption.Filter.Name });
                break;
            case nameof(MainViewModel.SelectedTimeOption):
                TimeFilterBox.SelectedItem = _vm.SelectedTimeOption;
                AppSettings.Update(s => s with { TimeFilter = _vm.SelectedTimeOption.Window.ToString() });
                break;
            case nameof(MainViewModel.SearchText):
                if (SearchBox.Text != _vm.SearchText) SearchBox.Text = _vm.SearchText;
                break;
            case nameof(MainViewModel.Sort):
                ShowSort();
                AppSettings.Update(s => s with
                {
                    SortField = _vm.BrowseSort.Field.ToString(),
                    SortReversed = _vm.BrowseSort.Reversed,
                    SearchSortField = _vm.SearchSort.Field.ToString(),
                    SearchSortReversed = _vm.SearchSort.Reversed,
                });
                break;
        }
    }

    // ---------------------------------------------------------------- status

    private void ShowStatus(StatusMessage status)
    {
        StatusRun.Text = status.Text;
        var hasAction = status.ActionLabel is not null;
        StatusGap.Text = hasAction ? "　" : string.Empty;
        StatusLinkRun.Text = status.ActionLabel ?? string.Empty;
        StatusLink.IsEnabled = hasAction;
        StatusLine.ToolTip = status.Text.Length > 50 ? status.Text : null;
        Announce(StatusLine);

        (StatusGlyph.Text, StatusGlyph.Foreground, StatusRun.Foreground) = status.Tone switch
        {
            StatusTone.Busy => ("\uE895", Palette.Info, Palette.Muted),
            StatusTone.Success => ("\uE73E", Palette.Success, Palette.Muted),
            StatusTone.Warning => ("\uE7BA", Palette.Warn, Palette.Warn),
            StatusTone.Error => ("\uEA39", Palette.Error, Palette.Error),
            _ => ("\uE946", Palette.Faint, Palette.Muted),
        };
    }

    private async void StatusLink_Click(object sender, RoutedEventArgs e) => await _vm.RunStatusActionAsync();

    /// <summary>
    /// Tells a screen reader the status changed. The status bar is where every result lands
    /// ("已收起 3 条", "同步完成"); without this a blind user hears nothing after acting.
    /// </summary>
    internal static void Announce(UIElement element) =>
        (System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(element)
         ?? System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(element))
        ?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);

    private void SetSyncing(bool syncing)
    {
        SyncLabel.Text = syncing ? "同步中…" : "同步";
        ShowSyncSpin();
    }

    /// <summary>The sync glyph's spinning while a sync runs; null while it stands still.</summary>
    private AnimationClock? _syncSpin;

    /// <summary>
    /// Spins the sync glyph while a sync runs and the window can be seen. The clock is stopped,
    /// not just taken off the glyph: WPF keeps ticking a detached clock that repeats forever and
    /// draws the window sixty times a second until the garbage collector happens to take it —
    /// minutes of an idle window drawing frames after every sync. A minimised or hidden window
    /// spins nothing (WPF would still draw each frame for it).
    /// </summary>
    private void ShowSyncSpin()
    {
        // Someone who turned animations off in Windows gets the words without the spinning.
        var spin = _vm.IsSyncing && SystemParameters.ClientAreaAnimation
                   && IsVisible && WindowState != WindowState.Minimized;
        if (spin == (_syncSpin is not null)) return;
        if (spin)
        {
            _syncSpin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = RepeatBehavior.Forever }
                .CreateClock();
            SyncSpin.ApplyAnimationClock(RotateTransform.AngleProperty, _syncSpin);
        }
        else
        {
            _syncSpin!.Controller?.Stop();
            SyncSpin.ApplyAnimationClock(RotateTransform.AngleProperty, null);
            _syncSpin = null;
        }
    }

    private void ShowEmpty(EmptyState? empty)
    {
        EmptyPanel.Visibility = empty is null ? Visibility.Collapsed : Visibility.Visible;
        if (empty is null) return;
        EmptyTitle.Text = empty.Title;
        EmptyDetail.Text = empty.Detail;
        EmptyDetail.Visibility = empty.Detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyActionButton.Content = empty.ActionLabel;
        EmptyActionButton.Visibility = empty.ActionLabel is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void EmptyAction_Click(object sender, RoutedEventArgs e)
    {
        switch (_vm.Empty?.Action)
        {
            case EmptyAction.ClearSearch:
                ClearSearch();
                break;
            case EmptyAction.ClearFilters:
                _vm.ClearFilters();
                break;
            case EmptyAction.ShowActive:
                ActiveTab.IsChecked = true;
                break;
            case EmptyAction.Sync:
                _ = _vm.SyncAsync();
                break;
        }
    }

    // ------------------------------------------------------------ keyboard

    /// <summary>
    /// Shortcuts that work wherever focus is. Ctrl+Z is left to a text box being edited —
    /// there it means "undo my typing", which is what the person pressing it expects.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var editing = Keyboard.FocusedElement is TextBox { IsReadOnly: false };
        switch (e.Key)
        {
            case Key.F when mods == ModifierKeys.Control:
                FocusSearch();
                e.Handled = true;
                break;
            case Key.F5 when mods == ModifierKeys.None:
                _ = _vm.SyncAsync();
                e.Handled = true;
                break;
            case Key.F1 when mods == ModifierKeys.None:
                ShowShortcuts();
                e.Handled = true;
                break;
            case Key.Z when mods == ModifierKeys.Control && !editing:
                _ = _vm.UndoAsync();
                e.Handled = true;
                break;
            case Key.D1 or Key.NumPad1 when mods == ModifierKeys.Control:
                ActiveTab.IsChecked = true;
                e.Handled = true;
                break;
            case Key.O when mods == ModifierKeys.Control:
                if (SingleSelected() is not null) OpenInApp_Click(this, e);
                e.Handled = true;
                break;
            case Key.D2 or Key.NumPad2 when mods == ModifierKeys.Control:
                ArchivedTab.IsChecked = true;
                e.Handled = true;
                break;
        }
        base.OnPreviewKeyDown(e);
    }

    private void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (SearchHistoryPopup.IsOpen && SearchHistoryKey(e)) return;
        switch (e.Key)
        {
            // Alt+↓ opens a drop-down, here as anywhere in Windows.
            case Key.System when e.SystemKey == Key.Down:
                OpenSearchHistory();
                e.Handled = true;
                break;
            case Key.Escape:
                if (SearchBox.Text.Length > 0) ClearSearch();
                else FocusList();
                e.Handled = true;
                break;
            // From the search box straight into the results, without reaching for the mouse.
            case Key.Down or Key.Enter:
                FocusList();
                e.Handled = true;
                break;
        }
    }

    private void SessionList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.Enter when mods == ModifierKeys.None:
                OpenTranscript();
                e.Handled = true;
                break;
            // Where the conversation stands now, rather than where it began.
            case Key.Enter when mods == ModifierKeys.Shift:
                OpenTranscript(jumpToLatest: true);
                e.Handled = true;
                break;
            case Key.Delete when mods == ModifierKeys.None:
                if (!_vm.ShowArchivedView) _ = ApplyVisibility(restore: false);
                e.Handled = true;
                break;
            case Key.G when mods == ModifierKeys.Control:
                AssignGroup_Click(sender, e);
                e.Handled = true;
                break;
            case Key.F2 when mods == ModifierKeys.None:
                Rename();
                e.Handled = true;
                break;
            case Key.C when mods == ModifierKeys.Control:
                CopyTitle_Click(sender, e);
                e.Handled = true;
                break;
            case Key.Escape:
                if (SearchBox.Text.Length > 0) ClearSearch();
                else SessionList.UnselectAll();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Typing while the list has focus starts a search: no need to find the box first.</summary>
    private void SessionList_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Text) || e.Text.Any(char.IsControl)) return;
        SearchBox.Focus();
        SearchBox.Text += e.Text;
        SearchBox.CaretIndex = SearchBox.Text.Length;
        e.Handled = true;
    }

    private void FocusList()
    {
        if (_vm.Sessions.Count == 0) return;
        if (SessionList.SelectedItem is null) SessionList.SelectedIndex = 0;
        SessionList.ScrollIntoView(SessionList.SelectedItem!);
        SessionList.UpdateLayout();
        if (ContainerFor(SessionList.SelectedItem!) is { } item)
            item.Focus();
        else
            SessionList.Focus();
    }

    /// <summary>What this is, which version, and what it does and does not do with the user's data.</summary>
    private void About_Click(object sender, RoutedEventArgs e)
    {
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "?";
        MessageDialog.Show(this, DialogKind.Info, $"会话管家 {version}",
            "把本机 Claude（桌面 Code、终端 CLI）、ChatGPT（Codex）与 Gemini CLI 的对话记录整理成一个目录："
            + "搜索、排序、分组、查看正文，或用各自的命令行接着聊。\n\n"
            + "· 完全在本机运行，不联网，不上传任何内容\n"
            + "· 对 Claude 与 ChatGPT 的数据只读，从不修改\n"
            + $"· 本工具自己的数据只写在 {_vm.DataRoot}\n\n"
            + "独立第三方工具，不是 Anthropic 或 OpenAI 的产品。");
    }

    private void ShowShortcuts() => MessageDialog.Show(this, DialogKind.Info, "键盘快捷键",
        "列表\n"
        + "  Ctrl+F　　搜索（在列表里直接打字也会开始搜索）\n"
        + "  Esc　　　清除搜索\n"
        + "  ↓ / Enter　从搜索框进入结果列表\n"
        + "  Alt+↓　　最近的搜索：↑↓ 选，Enter 搜，Delete 删掉一条\n"
        + "  Enter　　打开选中会话的对话记录\n"
        + "  Shift+Enter　打开并直接跳到最新\n"
        + "  Ctrl+O　　在 Claude / ChatGPT 中打开选中的会话\n"
        + "  Ctrl+G　　设为分组\n"
        + "  Delete　　收起到本工具归档\n"
        + "  Ctrl+Z　　撤销上一次整理\n"
        + "  Ctrl+C　　复制选中会话的标题\n"
        + "  Ctrl+1 / 2　切换 活跃 / 本工具归档\n"
        + "  F5　　　同步\n"
        + $"  {HotkeyName}　在任何地方唤出 / 收起会话管家（被占用时自动换一个，见「…」菜单）\n\n"
        + "对话记录窗口\n"
        + "  Ctrl+F　　在对话中查找，F3 / Shift+F3 下一个 / 上一个\n"
        + "  Ctrl+Home / Ctrl+End　跳到开头 / 最新\n"
        + "  Ctrl+O　　在 Claude / ChatGPT 中打开这条对话\n"
        + "  Ctrl+滚轮、Ctrl + / Ctrl -　缩放字号，Ctrl+0 还原\n"
        + "  Esc　　　关闭窗口");

    // --------------------------------------------------------------- launch

    private async void OpenClaude_Click(object sender, RoutedEventArgs e)
    {
        var result = await _vm.OpenClaudeAsync();
        // A failed launch reports the real reason; it never silently falls back to a browser.
        if (result.Outcome is LaunchOutcome.PackageNotFound or LaunchOutcome.IdentityMismatch
            or LaunchOutcome.ShellUnavailable)
            MessageDialog.Show(this, DialogKind.Warning, "无法启动官方 Claude", result.Message);
    }

    private async void OpenChatGpt_Click(object sender, RoutedEventArgs e)
    {
        var result = await _vm.OpenChatGptAsync();
        if (result.Outcome is LaunchOutcome.PackageNotFound or LaunchOutcome.IdentityMismatch
            or LaunchOutcome.ShellUnavailable)
            MessageDialog.Show(this, DialogKind.Warning, "无法启动 ChatGPT", result.Message);
    }

    private async void OpenDeepSeek_Click(object sender, RoutedEventArgs e)
    {
        var result = await _vm.OpenDeepSeekAsync();
        if (result.Outcome is LaunchOutcome.PackageNotFound or LaunchOutcome.ShellUnavailable)
            MessageDialog.Show(this, DialogKind.Warning, "无法启动 DeepSeek Harness", result.Message);
    }

    private async void Sync_Click(object sender, RoutedEventArgs e) => await _vm.SyncAsync();

    // ------------------------------------------------------- search & filter

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var empty = SearchBox.Text.Length == 0;
        SearchHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        SearchShortcutHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ClearSearchButton.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        _vm.SearchText = SearchBox.Text;
        ShowMineOnlyChoice();
        NoteSearchTyped(empty);
    }

    /// <summary>「只搜我说的」 shows while a search reaches what was said; the text stops short of it.</summary>
    private void ShowMineOnlyChoice()
    {
        var show = _vm.SearchesContent && !string.IsNullOrWhiteSpace(SearchBox.Text);
        MineOnlyToggle.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        MineOnlyToggle.IsChecked = _vm.SearchMineOnly;
        SearchBox.Padding = new Thickness(30, 6, show ? 32 + MineOnlyWidth : 32, 6);
    }

    /// <summary>Room the chip takes at the right of the search text, its gap included.</summary>
    private const double MineOnlyWidth = 84;

    /// <summary>Checked and unchecked rather than clicked: a screen reader toggles without a click.</summary>
    private void MineOnly_Changed(object sender, RoutedEventArgs e) =>
        _vm.SearchMineOnly = MineOnlyToggle.IsChecked == true;

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        ClearSearch();
        SearchBox.Focus();
    }

    private void ClearSearch() => SearchBox.Text = string.Empty;

    // -------------------------------------------------------- search history

    /// <summary>A search counts as run once it has stood this long — or when the box loses focus first.</summary>
    private readonly DispatcherTimer _searchStood = new() { Interval = TimeSpan.FromSeconds(2) };

    private readonly SearchHistory.Typing _searchTyping = new();

    /// <summary>The id 「定位」 put in the box: a search nobody typed, so none to remember.</summary>
    private string? _locateSearch;

    /// <summary>When the list under the box last closed: the click that closed it does not open it again.</summary>
    private long _searchHistoryClosedAt;

    private void WireSearchHistory()
    {
        _searchStood.Tick += (_, _) => RememberSearch();
        SearchBox.LostKeyboardFocus += (_, _) =>
        {
            SearchHistoryPopup.IsOpen = false;
            RememberSearch();
        };
        Deactivated += (_, _) => SearchHistoryPopup.IsOpen = false;
        SearchHistoryList.Picked += PickSearch;
        SearchHistoryList.Removed += ForgetSearch;
        SearchHistoryList.ClearedAll += ForgetAllSearches;
        ShowSearchHistoryButton();
    }

    /// <summary>The box changed: the list under it closes, and the new search is remembered once it has stood a moment.</summary>
    private void NoteSearchTyped(bool empty)
    {
        if (_locateSearch is not null && SearchBox.Text != _locateSearch) _locateSearch = null;
        _searchStood.Stop();
        if (empty) _searchTyping.StartOver();
        else
        {
            SearchHistoryPopup.IsOpen = false;
            if (_locateSearch is null) _searchStood.Start();
        }
        ShowSearchHistoryButton();
    }

    /// <summary>
    /// Keeps the search in the box in the list under it, once it has been run: it stood a
    /// moment, or the box lost focus — to the results, a row clicked, another window. Each later
    /// stage of a search typed on replaces the one kept before it.
    /// </summary>
    private void RememberSearch()
    {
        _searchStood.Stop();
        var settings = AppSettings.Current;
        if (!settings.RememberSearches || _locateSearch is not null) return;
        var updated = _searchTyping.Keep(settings.RecentSearches, SearchBox.Text);
        if (!updated.SequenceEqual(settings.RecentSearches)) AppSettings.Update(s => s with { RecentSearches = updated });
    }

    /// <summary>Opens the recent searches under the box; false when there are none to offer.</summary>
    private bool OpenSearchHistory()
    {
        var settings = AppSettings.Current;
        if (!settings.RememberSearches || settings.RecentSearches.Count == 0) return false;
        SearchHistoryList.Width = SearchBox.ActualWidth;
        SearchHistoryList.Show(settings.RecentSearches);
        SearchHistoryPopup.IsOpen = true;
        return true;
    }

    private bool SearchHistoryJustClosed => Environment.TickCount64 - _searchHistoryClosedAt < 300;

    private void SearchHistoryPopup_Closed(object? sender, EventArgs e) => _searchHistoryClosedAt = Environment.TickCount64;

    /// <summary>A click into the empty box offers the recent searches, as Windows' own search boxes do.</summary>
    private void SearchBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (SearchBox.Text.Length == 0 && !SearchHistoryPopup.IsOpen && !SearchHistoryJustClosed)
            Dispatcher.BeginInvoke(() => { OpenSearchHistory(); }, DispatcherPriority.Input);
    }

    private void SearchHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (SearchHistoryJustClosed) return;        // this click is the one that closed it
        SearchBox.Focus();
        OpenSearchHistory();
    }

    /// <summary>While the list is open: ↑↓ move through it, Enter runs one, Delete removes it, Esc closes it.</summary>
    private bool SearchHistoryKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                SearchHistoryList.MoveHighlight(1);
                break;
            case Key.Up:
                SearchHistoryList.MoveHighlight(-1);
                break;
            case Key.Enter when SearchHistoryList.Highlighted is { } picked:
                PickSearch(picked);
                break;
            case Key.Delete when SearchHistoryList.Highlighted is { } gone:
                ForgetSearch(gone);
                break;
            case Key.Escape:
            case Key.System when e.SystemKey is Key.Down or Key.Up:
                SearchHistoryPopup.IsOpen = false;
                break;
            default:
                return false;       // typing, Tab, Enter with nothing picked: as without the list
        }
        e.Handled = true;
        return true;
    }

    /// <summary>A recent search run again: back in the box, and first in the list once more.</summary>
    private void PickSearch(string search)
    {
        SearchHistoryPopup.IsOpen = false;
        _searchTyping.StartOver();
        SearchBox.Text = search;
        SearchBox.CaretIndex = SearchBox.Text.Length;
        SearchBox.Focus();
        RememberSearch();
    }

    private void ForgetSearch(string search)
    {
        var row = SearchHistoryList.HighlightedIndex;
        var updated = SearchHistory.Remove(AppSettings.Current.RecentSearches, search);
        AppSettings.Update(s => s with { RecentSearches = updated });
        _searchTyping.StartOver();
        if (updated.Count == 0) SearchHistoryPopup.IsOpen = false;
        else SearchHistoryList.Show(updated, highlight: row);
        ShowSearchHistoryButton();
    }

    private void ForgetAllSearches()
    {
        var forgotten = AppSettings.Current.RecentSearches;
        AppSettings.Update(s => s with { RecentSearches = [] });
        _searchTyping.StartOver();
        SearchHistoryPopup.IsOpen = false;
        ShowSearchHistoryButton();
        _vm.Report($"已清除 {forgotten.Count} 条搜索历史", StatusTone.Success, "撤销", () =>
        {
            AppSettings.Update(s => s with { RecentSearches = SearchHistory.Clean(s.RecentSearches.Concat(forgotten)) });
            ShowSearchHistoryButton();
            _vm.Report("搜索历史已恢复", StatusTone.Success);
            return Task.CompletedTask;
        });
    }

    /// <summary>The clock at the end of an empty box, while there are searches to offer.</summary>
    private void ShowSearchHistoryButton()
    {
        var settings = AppSettings.Current;
        var show = SearchBox.Text.Length == 0 && settings.RememberSearches && settings.RecentSearches.Count > 0;
        SearchHistoryButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SearchShortcutHint.Margin = new Thickness(0, 0, show ? 34 : 10, 0);
        SearchHint.Margin = new Thickness(32, 0, show ? 96 : 70, 0);
    }

    private void RememberSearchesSetting_Click(object sender, RoutedEventArgs e)
    {
        var on = MenuRememberSearches.IsChecked;
        var forgotten = AppSettings.Current.RecentSearches.Count;
        AppSettings.Update(s => s with { RememberSearches = on, RecentSearches = on ? s.RecentSearches : [] });
        _searchTyping.StartOver();
        SearchHistoryPopup.IsOpen = false;
        ShowSearchHistoryButton();
        _vm.Report(on
            ? "之后的搜索会记下来：点空的搜索框或按 Alt+↓ 再选"
            : forgotten > 0 ? $"不再记住搜索，已记下的 {forgotten} 条已删除" : "不再记住搜索", StatusTone.Success);
    }

    private void ViewTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _vm.ShowArchivedView = ArchivedTab.IsChecked == true;
        UpdateSelectionUi();
    }

    /// <summary>
    /// The box clears its selection while the filter list is being rebuilt; that null is not
    /// a choice the user made, so it is ignored rather than treated as "all".
    /// </summary>
    private void GroupFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GroupFilterBox.SelectedItem is GroupOptionViewModel option)
            _vm.SelectedGroupOption = option;
    }

    private void BackendFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BackendFilterBox.SelectedItem is BackendOptionViewModel option)
            _vm.SelectedBackendOption = option;
    }

    private void TimeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TimeFilterBox.SelectedItem is TimeOptionViewModel option)
            _vm.SelectedTimeOption = option;
    }

    // ------------------------------------------------------ pin, name, note

    private async void Pin_Click(object sender, RoutedEventArgs e)
    {
        var selected = Selected();
        if (selected.Count == 0) return;
        // A mixed selection is pinned; one that is all pinned already is unpinned.
        await _vm.SetPinnedAsync(selected, !selected.All(r => r.IsPinned));
    }

    private void Rename_Click(object sender, RoutedEventArgs e) => Rename();

    private async void Rename()
    {
        if (SingleSelected() is not { } row)
        {
            _vm.Report(SessionList.SelectedItems.Count > 1 ? "一次只能给一条会话改名" : "先在列表里选中一条会话");
            return;
        }
        var (result, text) = TextPromptDialog.Ask(this, "重命名（只在本工具里）",
            $"给这条会话起一个自己的名字，方便认出它。{row.AppName} 里的标题不会改变，仍是：「{row.Metadata.Title}」",
            row.Title.StartsWith("（未解析出标题）", StringComparison.Ordinal) ? string.Empty : row.Title,
            "只改本工具里显示的名字，搜索也会用它；可以撤销（Ctrl+Z）",
            row.HasLocalTitle ? "恢复原标题" : null);
        if (result == TextPromptDialog.Outcome.Saved) await _vm.RenameAsync(row, text);
        else if (result == TextPromptDialog.Outcome.Cleared) await _vm.RenameAsync(row, null);
    }

    private async void Note_Click(object sender, RoutedEventArgs e)
    {
        if (SingleSelected() is not { } row)
        {
            _vm.Report(SessionList.SelectedItems.Count > 1 ? "一次只能给一条会话写备注" : "先在列表里选中一条会话");
            return;
        }
        var (result, text) = TextPromptDialog.Ask(this, "备注",
            $"给「{row.Title}」写几句备注：做到哪了、下一步、为什么留着它……",
            row.Note ?? string.Empty, "只存在本工具里；搜索能找到备注里的字",
            row.HasNote ? "删除备注" : null, multiline: true, maxLength: 2000);
        if (result == TextPromptDialog.Outcome.Saved) await _vm.SetNoteAsync(row, text);
        else if (result == TextPromptDialog.Outcome.Cleared) await _vm.SetNoteAsync(row, null);
    }

    // ----------------------------------------------------------------- list

    private List<SessionRowViewModel> Selected() =>
        SessionList.SelectedItems.Cast<SessionRowViewModel>().ToList();

    private SessionRowViewModel? SingleSelected() =>
        SessionList.SelectedItems.Count == 1 ? SessionList.SelectedItem as SessionRowViewModel : null;

    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_restoringSelection) UpdateSelectionUi();
    }

    /// <summary>Set while replaced rows get their selection back; the panel waits for the result.</summary>
    private bool _restoringSelection;

    /// <summary>
    /// The side panel follows the selection: what is selected, in full, and only the actions
    /// that can act on it. A disabled action keeps its tooltip, which says why.
    /// </summary>
    private void UpdateSelectionUi()
    {
        var selected = Selected();
        var one = selected.Count == 1 ? selected[0] : null;

        DetailNone.Visibility = selected.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DetailOne.Visibility = one is not null ? Visibility.Visible : Visibility.Collapsed;
        DetailMany.Visibility = selected.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        DetailOne.DataContext = one;
        ShowPreview(one);
        if (selected.Count > 1)
        {
            ManyTitle.Text = $"已选中 {selected.Count} 条会话";
            ManyDetail.Text = string.Join("　·　", selected.GroupBy(r => r.Backend)
                                  .Select(g => $"{g.Key} {g.Count()}"))
                + "\n可以一起设为分组、移出分组或收起。查看记录与继续会话一次只针对一条。";
        }

        var singleHint = selected.Count == 0 ? "先在列表里选中一条会话" : "一次只能对一条会话这样做";

        ViewTranscriptButton.IsEnabled = one is not null;
        ViewTranscriptButton.ToolTip = one is null ? singleHint : "双击列表中的一行也可以打开";

        OpenInAppButton.IsEnabled = one?.CanOpenInApp == true;
        OpenInAppLabel.Text = one?.OpenInAppShortLabel ?? "在应用中打开";
        OpenInAppButton.ToolTip = one is null ? singleHint : one.OpenInAppLabel + "：" + one.OpenInAppTooltip;

        ResumeButton.IsEnabled = one?.CanResume == true;
        ResumeLabel.Text = one?.ResumeShortLabel ?? "命令行继续";
        ResumeButton.ToolTip = one?.ResumeTooltip ?? singleHint;

        var any = selected.Count > 0;
        AssignGroupButton.IsEnabled = any;
        ClearGroupButton.IsEnabled = any;
        ArchiveButton.IsEnabled = any;
        ArchiveLabel.Text = _vm.ShowArchivedView ? "恢复到活跃" : "收起到归档";
        ArchiveKeyHint.Visibility = _vm.ShowArchivedView ? Visibility.Collapsed : Visibility.Visible;
        System.Windows.Automation.AutomationProperties.SetName(ArchiveButton, ArchiveLabel.Text);
        ArchiveButton.ToolTip = !any ? "先在列表里选中会话，可多选"
            : _vm.ShowArchivedView ? "放回活跃列表；只改本工具的列表"
            : "从活跃列表收起；只改本工具的列表，Claude / ChatGPT 侧不变，可撤销";
    }

    private void SessionList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var selected = Selected();
        if (selected.Count == 0)
        {
            e.Handled = true;       // nothing to act on: no menu rather than a menu of dead entries
            return;
        }
        var one = selected.Count == 1 ? selected[0] : null;

        MenuView.IsEnabled = one is not null;
        MenuOpenInApp.IsEnabled = one?.CanOpenInApp == true;
        MenuOpenInApp.Header = one?.OpenInAppLabel ?? "在应用中打开此会话";
        MenuResume.IsEnabled = one?.CanResume == true;
        MenuResume.Header = one?.ResumeLabel ?? "用命令行继续此会话";
        MenuCopyTitle.Header = selected.Count > 1 ? $"复制 {selected.Count} 个标题" : "复制标题";
        MenuCopyId.IsEnabled = one is not null;
        MenuCopyPath.IsEnabled = one?.HasProject == true;
        MenuOpenFolder.IsEnabled = one?.ProjectLooksOpenable == true;
        MenuReveal.IsEnabled = one?.HasTranscriptFile == true;
        MenuArchive.Header = _vm.ShowArchivedView ? "恢复到活跃列表" : "收起到本工具归档";
        MenuArchive.InputGestureText = _vm.ShowArchivedView ? string.Empty : "Delete";
        MenuDraft.IsEnabled = one is not null;
        MenuPin.Header = selected.All(r => r.IsPinned)
            ? (selected.Count > 1 ? $"取消置顶 {selected.Count} 条" : "取消置顶")
            : (selected.Count > 1 ? $"置顶 {selected.Count} 条" : "置顶");
        MenuRename.IsEnabled = one is not null;
        MenuNote.IsEnabled = one is not null;
    }

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Reset) return;

        var keys = _reselect;
        var index = _reselectIndex;
        var offset = _restoreOffset;
        var hadFocus = _listHadFocus;
        _reselect = null;
        _reselectIndex = -1;
        _restoreOffset = null;

        // Once the ListView has processed the reset, put the same conversations back — and,
        // for a refresh of the same view, the same scroll position.
        Dispatcher.InvokeAsync(() =>
        {
            try
            {
                if (offset is { } o) _listScroll?.ScrollToVerticalOffset(o);
                else _listScroll?.ScrollToTop();

                var restored = 0;
                if (keys is { Count: > 0 })
                {
                    foreach (var row in _vm.Sessions)
                    {
                        if (!keys.Contains(row.Metadata.Key)) continue;
                        SessionList.SelectedItems.Add(row);
                        restored++;
                        if (row.Metadata.Key == _locate)
                        {
                            _locate = null;         // found and shown
                            SessionList.ScrollIntoView(row);
                        }
                    }
                }

                // The selected rows left this view (collapsed, restored, grouped out of the filter):
                // select the row that took their place, so a keyboard user can carry straight on.
                if (restored == 0 && offset is not null && index >= 0 && _vm.Sessions.Count > 0)
                    SessionList.SelectedIndex = Math.Min(index, _vm.Sessions.Count - 1);

                if (hadFocus && SessionList.SelectedItem is { } selected && ContainerFor(selected) is { } item)
                    item.Focus();
                else if (offset is null && SessionList.SelectedItem is { } kept)
                    SessionList.ScrollIntoView(kept);      // a re-sort keeps the selection in sight
            }
            finally
            {
                // Whatever happened above, the panel follows the selection again from here.
                _restoringSelection = false;
                UpdateSelectionUi();
            }
        }, DispatcherPriority.Loaded);
    }

    private void SessionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only a double-click on a row opens it; the header and empty space do nothing.
        if (e.OriginalSource is DependencyObject source && FindRow(source) is not null) OpenTranscript();
    }

    /// <summary>
    /// Right-clicking a row selects it first. Without this the context menu would act on
    /// whatever was selected before, which is never what the click meant.
    /// </summary>
    private void SessionList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (FindRow(source) is not { } row) return;
        if (row.IsSelected) return;        // keep an existing multi-row selection intact

        SessionList.SelectedItems.Clear();
        row.IsSelected = true;
    }

    private static ListViewItem? FindRow(DependencyObject source)
    {
        for (var node = source; node is not null; node = Parent(node))
        {
            if (node is ListViewItem item) return item;
            if (node is ListView) return null;
        }
        return null;

        static DependencyObject? Parent(DependencyObject node) =>
            node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
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

    /// <summary>
    /// Near the bottom, the next page loads by itself: paging is how the catalog stays fast,
    /// not something the person scrolling should have to operate.
    /// </summary>
    private void OnListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // ScrollChanged bubbles: the column header keeps a scroll viewer of its own inside
        // this one, and its "I am at my end" would read as the list reaching the bottom.
        if (!ReferenceEquals(e.OriginalSource, _listScroll)) return;
        if (e.ViewportWidthChange != 0) FitTitleColumn();
        if (_vm.HasMorePages && e.ViewportHeight > 0 && e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 10)
            _ = _vm.LoadMoreAsync();
    }

    private async void LoadMore_Click(object sender, RoutedEventArgs e) => await _vm.LoadMoreAsync();

    // --------------------------------------------------------------- sorting

    private readonly Dictionary<SortField, (GridViewColumn Column, TextBlock Header, string Label)> _sortHeaders = [];

    /// <summary>
    /// Each header is its label, a sort arrow and a tooltip that says what clicking does —
    /// built here so all six behave alike.
    /// </summary>
    private void BuildColumnHeaders()
    {
        Add(SortField.Title, TitleColumn, "标题", "点击按标题排序（中文按拼音），再点一次反向");
        Add(SortField.Group, GroupColumn, "分组",
            "白色：本工具的分组　蓝色：应用自己的分组　灰色「—」：未分组\n点击按分组排序并分段显示，再点一次反向");
        Add(SortField.Source, SourceColumn, "来源", "点击按来源分段：Claude（桌面、终端、网页）、ChatGPT（Codex、网页）、Gemini、DeepSeek 依次排开");
        Add(SortField.Project, ProjectColumn, "工作目录",
            "列宽不够时保留末尾的目录名，悬停一行看完整路径\n点击按工作目录排序并分段显示");
        Add(SortField.LastActivity, TimeColumn, "最后活动", "默认按最后活动从新到旧；再点一次从旧到新");
        Add(SortField.Size, SizeColumn, "大小",
            "记录文件的大小（多半是工具输出）；悬停一行看精确字节数\n点击按大小排序，从大到小，再点一次反向");
        Add(SortField.AppState, StateColumn, "应用内",
            "这条会话在它自己的应用里的状态（读自应用文件，本工具不改动）\n点击按状态分段：活跃、已归档、仅终端");

        void Add(SortField field, GridViewColumn column, string label, string tooltip)
        {
            var header = new TextBlock { ToolTip = tooltip, Text = label };
            column.Header = header;
            _sortHeaders[field] = (column, header, label);
        }
    }

    /// <summary>The arrow on the sorted column, and the picker, both say the same thing as the list.</summary>
    private void ShowSort()
    {
        var sort = _vm.Sort;
        foreach (var (field, (_, header, label)) in _sortHeaders)
        {
            header.Inlines.Clear();
            header.Inlines.Add(new Run(label));
            if (field != sort.Field)
            {
                // Clear rather than null: a null brush is "no text", not "the header's usual colour".
                header.ClearValue(TextBlock.ForegroundProperty);
                continue;
            }

            // Only the sorted column carries an arrow. An empty icon-font run on the others
            // rendered as two "missing glyph" boxes after every label.
            header.Foreground = Palette.Info;
            header.Inlines.Add(new Run(" "));
            header.Inlines.Add(new Run(sort.Descending ? "\uE70D" : "\uE70E")
            {
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 9,
            });
        }

        // Searching offers 相关度 too; the list is swapped before the selection is shown in it.
        var choices = _vm.IsSearching ? SortOptionViewModel.Searching : SortOptionViewModel.All;
        if (!ReferenceEquals(SortBox.ItemsSource, choices)) SortBox.ItemsSource = choices;
        var option = SortOptionViewModel.For(sort.Field);
        if (!ReferenceEquals(SortBox.SelectedItem, option)) SortBox.SelectedItem = option;
        SortDirectionButton.Content = sort.Descending ? "\uE74B" : "\uE74A";
        SortDirectionButton.ToolTip = $"当前：{option.Display}，{option.Direction(sort.Reversed)}。点击改为{option.Direction(!sort.Reversed)}";
    }

    private void ColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } column }) return;     // the filler past the last column
        foreach (var (field, header) in _sortHeaders)
        {
            if (!ReferenceEquals(header.Column, column)) continue;
            _vm.SortBy(field);
            return;
        }
    }

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortBox.SelectedItem is SortOptionViewModel option && option.Field != _vm.Sort.Field)
            _vm.SetSort(new SortOrder(option.Field, false));
    }

    private void SortDirection_Click(object sender, RoutedEventArgs e) =>
        _vm.SetSort(_vm.Sort with { Reversed = !_vm.Sort.Reversed });

    /// <summary>
    /// Headings per category while the list is sorted by one. Switched just before the rows
    /// are replaced, so the old rows are never shown under headings they were not built with.
    /// </summary>
    private void ApplySections()
    {
        var view = CollectionViewSource.GetDefaultView(SessionList.ItemsSource);
        if (view?.GroupDescriptions is not { } groups) return;
        var grouped = groups.Count > 0;
        if (_vm.ShowSections == grouped) return;
        if (_vm.ShowSections) groups.Add(new PropertyGroupDescription(nameof(SessionRowViewModel.Section)));
        else groups.Clear();
    }

    /// <summary>
    /// The row's container. With sections on, rows live inside group containers the list's own
    /// generator does not hand out, so the visual tree is searched instead.
    /// </summary>
    private ListViewItem? ContainerFor(object item)
    {
        if (SessionList.ItemContainerGenerator.ContainerFromItem(item) is ListViewItem direct) return direct;
        return FindContainer(SessionList, item);

        static ListViewItem? FindContainer(DependencyObject root, object target)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is ListViewItem { Content: var content } found && ReferenceEquals(content, target)) return found;
                if (FindContainer(child, target) is { } nested) return nested;
            }
            return null;
        }
    }

    // --------------------------------------------------------------- columns

    private IEnumerable<(string Key, GridViewColumn Column)> ResizableColumns() =>
    [
        ("group", GroupColumn), ("source", SourceColumn), ("project", ProjectColumn),
        ("time", TimeColumn), ("size", SizeColumn), ("state", StateColumn),
    ];

    /// <summary>Widths the user dragged come back next time; any change refits the title column.</summary>
    private void RestoreColumnWidths()
    {
        var saved = AppSettings.Current.ColumnWidths;
        foreach (var (key, column) in ResizableColumns())
        {
            if (saved.TryGetValue(key, out var width)) column.Width = width;
            ((INotifyPropertyChanged)column).PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(GridViewColumn.ActualWidth)) return;
                Dispatcher.BeginInvoke(FitTitleColumn, DispatcherPriority.Background);
                AppSettings.Update(s => s with
                {
                    ColumnWidths = ResizableColumns().ToDictionary(c => c.Key, c => Math.Round(c.Column.ActualWidth)),
                });
            };
        }
    }

    private void SessionList_SizeChanged(object sender, SizeChangedEventArgs e) => FitTitleColumn();

    /// <summary>
    /// On a small screen (a 1366×768 laptop at 125 % is about 1090 wide) the side panel gives
    /// up some width so the titles keep enough to be read.
    /// </summary>
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        var width = sizeInfo.NewSize.Width < 1120 ? 300 : 348;
        if (Math.Abs(SidePanelColumn.Width.Value - width) > 0.5) SidePanelColumn.Width = new GridLength(width);
    }

    /// <summary>
    /// The title takes whatever width the other columns leave, so the list fills the window
    /// without a horizontal scroll bar and a wider window means longer visible titles.
    /// </summary>
    private void FitTitleColumn()
    {
        var viewport = _listScroll is { ViewportWidth: > 0 } scroll ? scroll.ViewportWidth : SessionList.ActualWidth - 14;
        var others = ResizableColumns().Sum(c => c.Column.ActualWidth);
        var width = Math.Max(180, Math.Floor(viewport - others - 12));
        if (Math.Abs(TitleColumn.Width - width) > 0.5) TitleColumn.Width = width;
    }

    // --------------------------------------------------------- transcript

    private void ViewTranscript_Click(object sender, RoutedEventArgs e) => OpenTranscript();

    private async void Changes_Click(object sender, RoutedEventArgs e)
    {
        if (SingleSelected() is not { } row) return;
        var path = await _vm.FindTranscriptAsync(row);
        if (path is null)
        {
            MessageDialog.Show(this, DialogKind.Info, "这条会话没有可读的记录", _vm.ExplainMissingTranscript(row));
            return;
        }
        ChangesWindow.ShowFor(this, row, path);
    }

    private async void RevealTranscript_Click(object sender, RoutedEventArgs e)
    {
        if (SingleSelected() is { } row) await _vm.RevealTranscriptAsync(row);
    }

    private void OpenTranscript(bool jumpToLatest = false)
    {
        if (SingleSelected() is not { } row)
        {
            _vm.Report(SessionList.SelectedItems.Count > 1 ? "一次只能打开一条会话的对话记录" : "先在列表里选中一条会话");
            return;
        }
        // Found by what was said in it: the reader opens looking for that.
        OpenTranscriptFor(row, jumpToLatest, find: jumpToLatest ? null : row.ContentTerm);
    }

    /// <summary>
    /// Opens one conversation's transcript — the selected one, or another part of it — over
    /// <paramref name="anchor"/>, or says why there is nothing to show.
    /// </summary>
    private async void OpenTranscriptFor(SessionRowViewModel row, bool jumpToLatest = false, string? find = null,
        Window? anchor = null)
    {
        var path = await _vm.FindTranscriptAsync(row);
        if (path is null)
        {
            MessageDialog.Show(anchor ?? this, DialogKind.Info, "这条会话没有可显示的正文", _vm.ExplainMissingTranscript(row));
            return;
        }

        // Whole-body reads happen here and nowhere else. Browsing the list reads at most the
        // tail of the one selected transcript, for its preview — bounded, cached, and dropped
        // as soon as the selection moves on — which keeps the list quick with hundreds on disk.
        TranscriptWindow.ShowFor(anchor ?? this, row, path, jumpToLatest, find);
    }

    // ------------------------------------------------------------- preview

    private CancellationTokenSource? _previewCts;
    private (ConversationKey Key, string Revision)? _previewFor;

    /// <summary>
    /// Fills 最近对话 for the one selected row. Arrowing through the list reads only where the
    /// user stops, and a refresh that re-selects the same, unchanged row keeps what is shown.
    /// </summary>
    private async void ShowPreview(SessionRowViewModel? row)
    {
        // What found the row can change while the row stays the same: a new search, same selection.
        _ = ShowContentHitsAsync(row);
        _ = ShowUsageAsync(row);
        _ = ShowContinuationAsync(row);
        var target = row is null ? ((ConversationKey, string)?)null : (row.Metadata.Key, row.Metadata.SourceRevision);
        if (target is not null && target == _previewFor) return;
        // The same conversation with something new in it keeps its lines until the new ones
        // are read, so an active conversation does not blink once a minute.
        var sameConversation = row is not null && _previewFor?.Key == row.Metadata.Key;
        _previewFor = target;
        _previewCts?.Cancel();
        PreviewSection.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        if (row is null) return;

        var cts = new CancellationTokenSource();
        _previewCts = cts;
        if (!sameConversation)
        {
            // Never the previous row's lines under this row's title.
            PreviewList.ItemsSource = null;
            PreviewNote.Visibility = Visibility.Collapsed;
            PreviewOpenButton.Visibility = Visibility.Collapsed;
        }
        try
        {
            await Task.Delay(sameConversation ? 0 : 140, cts.Token);
            var loading = _vm.PreviewAsync(row, cts.Token);
            // A slow read says so; a quick one does not flash a message first.
            if (!sameConversation && await Task.WhenAny(loading, Task.Delay(300, cts.Token)) != loading
                && !cts.IsCancellationRequested)
            {
                PreviewNote.Text = "正在读取最近的对话…";
                PreviewNote.Visibility = Visibility.Visible;
            }
            var preview = await loading;
            if (cts.IsCancellationRequested) return;

            PreviewList.ItemsSource = preview.Lines;
            PreviewNote.Text = preview.Note ?? string.Empty;
            PreviewNote.Visibility = preview.Note is null ? Visibility.Collapsed : Visibility.Visible;
            PreviewOpenButton.Visibility = preview.Lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            // The selection moved on; the newer call fills the panel.
        }
    }

    private void PreviewOpen_Click(object sender, RoutedEventArgs e) => OpenTranscript(jumpToLatest: true);

    private CancellationTokenSource? _hitsCts;
    private (ConversationKey Key, string Revision, string Term, bool ByUser, int Count)? _hitsFor;

    /// <summary>
    /// When a search found the row by what was said in it, the places it was said — the answer
    /// to "why is this here?" that its title cannot give.
    /// </summary>
    private async Task ShowContentHitsAsync(SessionRowViewModel? row)
    {
        var target = row?.ContentTerm is { } term
            ? (row.Metadata.Key, row.Metadata.SourceRevision, term, row.ContentByUser, row.ContentHitCount)
            : ((ConversationKey, string, string, bool, int)?)null;
        if (target is not null && target == _hitsFor) return;
        _hitsFor = target;
        _hitsCts?.Cancel();
        if (row?.ContentTerm is not { } word)
        {
            ContentHitsSection.Visibility = Visibility.Collapsed;
            ContentHitsList.ItemsSource = null;
            return;
        }

        var cts = new CancellationTokenSource();
        _hitsCts = cts;
        try
        {
            var hits = await _vm.ContentHitsAsync(row, cts.Token);
            if (cts.IsCancellationRequested) return;
            ContentHitsLabel.Text = (row.ContentByUser ? $"你说过的话里提到「{word}」" : $"对话里提到「{word}」")
                                    + (row.ContentHitCount > 0 ? $"　{row.ContentHitCount} 条消息" : "");
            ContentHitsList.ItemsSource = hits.Select(h => new PreviewHit(
                h.ByUser ? "你" : row.AssistantName, h.Stamp ?? string.Empty, ConversationPreview.Plain(h.Snippet), h.ByUser, word)).ToList();
            ContentHitsSection.Visibility = hits.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            // The selection moved on.
        }
    }

    private void ContentHitsOpen_Click(object sender, RoutedEventArgs e) => OpenTranscript();

    /// <summary>
    /// The selected conversation's tokens, once the usage ledger exists (the usage window was
    /// opened once; every sync keeps it up to date since). Nothing is shown otherwise.
    /// </summary>
    private async Task ShowUsageAsync(SessionRowViewModel? row)
    {
        var usage = row is null ? null : await _vm.UsageOfAsync(row);
        if (row is null || !ReferenceEquals(SingleSelected(), row) || usage is not { Calls: > 0 } t)
        {
            if (row is null || ReferenceEquals(SingleSelected(), row))
                UsageLabel.Visibility = UsageLine.Visibility = Visibility.Collapsed;
            return;
        }
        UsageValue.Text = $"{UsageWindow.Tokens(t.Total)} token　·　{t.Calls:N0} 次调用";
        UsageLine.ToolTip = $"输入 {UsageWindow.Tokens(t.Input)}　缓存读取 {UsageWindow.Tokens(t.CacheRead)}　"
                            + $"缓存写入 {UsageWindow.Tokens(t.CacheWrite)}　输出 {UsageWindow.Tokens(t.Output)}\n"
                            + (_vm.UsageUpdatedAt is { } at ? $"截至 {at:HH:mm} 的统计，" : "")
                            + "每次同步后自动更新；子代理、子任务算在内";
        UsageLabel.Visibility = UsageLine.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// For a conversation Claude carried on across transcripts, its other parts — a desktop
    /// session's earlier transcripts and the rest of that chain, the session a resumed copy
    /// came from, the copies resumed from it. Each is one click from being read, or found in
    /// the list. Other rows show nothing.
    /// </summary>
    private async Task ShowContinuationAsync(SessionRowViewModel? row)
    {
        Relatives relatives;
        try
        {
            relatives = row is null ? Relatives.None : await _vm.RelativesAsync(row);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException
                                       or Microsoft.Data.Sqlite.SqliteException)
        {
            relatives = Relatives.None;
        }
        // The selection moved on while the catalog answered: the newer call fills the panel.
        if (row is not null && !ReferenceEquals(SingleSelected(), row)) return;
        var entries = row is null ? [] : ContinuationEntry.For(row.Metadata, relatives, DateTime.Now);
        // A Codex task is forked, as ChatGPT calls it; a Claude conversation is continued.
        ContinuationLabel.Text = row?.IsCodex == true ? "分叉" : "续接";
        ContinuationList.ItemsSource = entries;
        ContinuationLabel.Visibility = ContinuationList.Visibility =
            entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ContinuationOpen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ContinuationEntry { Row: { } part })
            OpenTranscriptFor(new SessionRowViewModel(part));
    }

    private void ContinuationLocate_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ContinuationEntry { Row: { } part }) Locate(part);
    }

    // ----------------------------------------------------------- open in app

    private async void OpenInApp_Click(object sender, RoutedEventArgs e)
    {
        if (SingleSelected() is not { } row) return;
        if (!row.CanOpenInApp)
        {
            MessageDialog.Show(this, DialogKind.Info, "不能在应用中打开",
                row.Source.Open == OpenRoute.CodexLink ? "这条任务的 ID 不是 ChatGPT 能打开的格式。" : row.OpenInAppTooltip);
            return;
        }

        var result = await _vm.OpenInAppAsync(row);
        if (result.Outcome != DeepLinkOutcome.Requested)
        {
            MessageDialog.Show(this, DialogKind.Warning,
                row.Source.Open switch
                {
                    OpenRoute.CodexLink => "无法打开 ChatGPT",
                    OpenRoute.WebLink => "无法在浏览器中打开",
                    OpenRoute.AppOnly => $"无法启动 {row.AppName}",
                    _ => "无法在 Claude 中打开",
                },
                result.Message + (row.Source.Open == OpenRoute.ClaudeLink ? "\n\n" + ClaudeDeepLink.FailureHint : string.Empty));
        }
    }

    private async void Resume_Click(object sender, RoutedEventArgs e)
    {
        if (SingleSelected() is not { } row) return;
        if (!row.CanResume)
        {
            MessageDialog.Show(this, DialogKind.Info, "这条会话不能用命令行继续", row.ResumeTooltip);
            return;
        }

        var result = await _vm.ResumeCliAsync(row);
        if (result.Outcome != ResumeOutcome.Started)
            MessageDialog.Show(this, DialogKind.Warning, "无法恢复会话", result.Message);
    }

    // ------------------------------------------------------------- organise

    private async void ToggleArchive_Click(object sender, RoutedEventArgs e) =>
        await ApplyVisibility(restore: _vm.ShowArchivedView);

    /// <summary>All selected rows in one batch: one catalog transaction each, one reload at the end.</summary>
    private async Task ApplyVisibility(bool restore)
    {
        var selected = Selected();
        if (selected.Count == 0)
        {
            _vm.Report("先在列表里选中至少一条会话");
            return;
        }
        await _vm.SetVisibilityAsync(selected, restore);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var selected = Selected();
        if (selected.Count == 0)
        {
            _vm.Report("先在列表里选中至少一条会话");
            return;
        }
        var what = selected.Count == 1 ? $"「{selected[0].Title}」" : $"选中的 {selected.Count} 条对话";
        var choice = MessageDialog.Show(this, DialogKind.Warning, $"从会话管家删除{what}？",
            "会删掉会话管家里的这一行、它的副本和搜索索引里的内容，以后同步也不会再列出——即使应用里还有它。\n\n"
            + "Claude、ChatGPT、Gemini 自己的记录不受影响：会话管家从不改动应用的数据。这一步不能撤销。",
            primary: "删除", cancel: "取消");
        if (choice != DialogChoice.Primary) return;
        await _vm.DeleteAsync(selected);
    }

    private async void AssignGroup_Click(object sender, RoutedEventArgs e)
    {
        var selected = Selected();
        if (selected.Count == 0)
        {
            _vm.Report("先在列表里选中至少一条会话");
            return;
        }

        // When one row is selected its current group is offered, so the dialog doubles as a
        // rename for that conversation instead of always starting blank.
        var current = selected.Count == 1 ? selected[0].EffectiveGroup : null;
        var dialog = new GroupDialog(_vm.KnownGroups, selected.Count, current) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.GroupName is not { } name) return;

        await _vm.AssignGroupAsync(selected, name);
    }

    private async void ClearGroup_Click(object sender, RoutedEventArgs e)
    {
        var selected = Selected();
        if (selected.Count == 0)
        {
            _vm.Report("先在列表里选中至少一条会话");
            return;
        }
        await _vm.ClearGroupAsync(selected);
    }

    private async void GroupByProject_Click(object sender, RoutedEventArgs e)
    {
        var ungrouped = await _vm.CountUngroupedAsync();
        if (ungrouped == 0)
        {
            _vm.Report("当前视图里没有未分组的会话，不需要自动分组");
            return;
        }

        var choice = MessageDialog.Show(this, DialogKind.Question,
            $"按工作目录给 {ungrouped} 条会话分组？",
            "每条还没有分组的会话，会放进以它的工作目录命名的分组"
            + "（例如 D:\\projects\\demo →「demo」）。这个数字包括当前视图里所有应用的会话。\n\n"
            + "· 已经分过组的、以及你明确移出分组的，都不会被改动\n"
            + "· 只改本工具自己的分组，Claude / ChatGPT 侧不受影响\n"
            + "· 做完之后可以一步撤销（Ctrl+Z）",
            "开始分组", cancel: "取消");
        if (choice != DialogChoice.Primary) return;

        await _vm.GroupByProjectAsync();
    }

    // ------------------------------------------------------------ utilities

    private void CopyTitle_Click(object sender, RoutedEventArgs e)
    {
        var selected = Selected();
        if (selected.Count == 0) return;
        CopyToClipboard(string.Join(Environment.NewLine, selected.Select(r => r.Metadata.Title)),
            selected.Count > 1 ? $"{selected.Count} 个标题" : "标题");
    }

    private void CopyId_Click(object sender, RoutedEventArgs e)
    {
        if (SingleSelected() is { } row) CopyToClipboard(row.SessionId, "会话 ID");
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (SingleSelected() is { ProjectRef: { } path }) CopyToClipboard(path, "工作目录");
    }

    private void CopyToClipboard(string text, string what)
    {
        var error = Shell.Copy(text);
        _vm.Report(error is null ? $"已复制{what}" : "复制失败：" + error,
            error is null ? StatusTone.Success : StatusTone.Error);
    }

    private async void OpenProjectFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SingleSelected() is not { ProjectRef: { } path }) return;
        _vm.Report("正在打开工作目录…", StatusTone.Busy);
        var error = await Shell.OpenFolderAsync(path);
        _vm.Report(error ?? $"已打开 {path}", error is null ? StatusTone.Success : StatusTone.Warning);
    }

    private async void MemoryDraft_Click(object sender, RoutedEventArgs e)
    {
        if (SingleSelected() is not { } row)
        {
            _vm.Report("先在列表里选中一条会话");
            return;
        }

        if (await _vm.WriteMemoryDraftAsync(row) is not { } draft) return;

        // Opening the draft is the point: it exists to be read and cut down, not applied.
        var choice = MessageDialog.Show(this, DialogKind.Info,
            draft.Candidates == 0 ? "没有找到记忆候选" : $"找到 {draft.Candidates} 条记忆候选",
            $"扫描了你在这条会话里的 {draft.Scanned} 条发言。\n\n"
            + "草稿里每条都是从你自己的发言原样摘录的，没有总结改写。"
            + "确认哪些值得记之后，再用记忆工具写入 memory/；本工具不会替你写入。\n\n" + draft.File,
            "打开草稿", secondary: "打开所在文件夹", cancel: "关闭");
        var error = choice switch
        {
            DialogChoice.Primary => Shell.Open(draft.File),
            DialogChoice.Secondary => Shell.Reveal(draft.File),
            _ => null,
        };
        if (error is not null) _vm.Report("打开失败：" + error, StatusTone.Error);
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        // A path shown only in the status bar would have to be copied out by hand, which is
        // the step the user asked to skip: the folder opens by itself.
        if (await _vm.ExportSessionsAsync() is { } directory && Shell.Open(directory) is { } error)
            _vm.Report("已导出，但打开目录失败：" + error, StatusTone.Warning);
    }

    private void OpenExportFolder_Click(object sender, RoutedEventArgs e) =>
        OpenOwnFolder(LauncherPaths.ExportDirectory);

    private void OpenDataRoot_Click(object sender, RoutedEventArgs e) =>
        OpenOwnFolder(LauncherPaths.DataRoot);

    private void OpenOwnFolder(string folder)
    {
        System.IO.Directory.CreateDirectory(folder);
        if (Shell.Open(folder) is { } error) _vm.Report("打开失败：" + error, StatusTone.Error);
    }

    private void Shortcuts_Click(object sender, RoutedEventArgs e) => ShowShortcuts();

    // ------------------------------------------------------- hotkey & tray

    private GlobalHotkey? _hotkey;
    private TrayIcon? _tray;
    private bool _exitRequested;
    private bool _toldAboutTray;
    private bool _hotkeyTaken;

    /// <summary>The global hotkey in words, for menus and messages: whichever combination was free.</summary>
    private string HotkeyName => _hotkey?.Name ?? GlobalHotkey.Candidates[0].Name;

    /// <summary>
    /// The global hotkey and the tray icon, as the settings say. A second copy on a data folder
    /// of its own (CLAUDE_LAUNCHER_DATA_DIR) leaves the hotkey to the everyday one.
    /// </summary>
    private void ApplyBackgroundSettings()
    {
        var settings = AppSettings.Current;
        // CLAUDE_LAUNCHER_HOTKEY=1 lets such a copy take it anyway, to test it.
        var ownsHotkey = !LauncherPaths.DataRootFromVariable
                         || Environment.GetEnvironmentVariable("CLAUDE_LAUNCHER_HOTKEY") == "1";
        var wantHotkey = settings.GlobalHotkey && ownsHotkey;
        if (wantHotkey && _hotkey is null)
        {
            var preferred = GlobalHotkey.Parse(settings.Hotkey);
            _hotkey = GlobalHotkey.RegisterFirstFree(this, ToggleFromHotkey, preferred);
            _hotkeyTaken = _hotkey is null;
            if (_hotkey is null)
                _vm.Report("可用的全局快捷键都被其他程序占用了，这次没有启用；可在「⋯」菜单里换一个", StatusTone.Warning);
            else if (preferred is not null && _hotkey.Name != preferred.Name)
                _vm.Report($"你设的 {preferred.Name} 被其他程序占用，这次改用 {_hotkey.Name}", StatusTone.Warning);
        }
        else if (!wantHotkey && _hotkey is not null)
        {
            _hotkey.Dispose();
            _hotkey = null;
        }

        if (settings.CloseToTray)
        {
            _tray ??= new TrayIcon(this, "会话管家 · Claude / ChatGPT", ShowFromBackground,
            [
                new("打开会话管家", ShowFromBackground),
                new("同步", () => _ = _vm.SyncAsync()),
                null,
                new("退出", ExitFromTray),
            ]);
            _tray.Show();
        }
        else if (_tray is not null)
        {
            _tray.Dispose();
            _tray = null;
            if (!IsVisible) ShowFromBackground();       // never leave a hidden window with no way back
        }

        MenuHotkey.IsChecked = settings.GlobalHotkey;
        MenuHotkey.IsEnabled = ownsHotkey;
        MenuHotkeyChange.IsEnabled = ownsHotkey;
        MenuHotkey.Header = _hotkeyTaken && settings.GlobalHotkey
            ? "全局快捷键（都被其他程序占用）" : $"全局快捷键 {HotkeyName}";
        MenuHotkey.ToolTip = $"在任何地方按 {HotkeyName} 唤出会话管家，再按一次收起"
            + (_hotkey is not null && _hotkey.Name != GlobalHotkey.Candidates[0].Name
                ? $"\n{GlobalHotkey.Candidates[0].Name} 已被其他程序占用，所以用了 {_hotkey.Name}" : "")
            + "\n使用独立数据目录的副本不注册它";
        MenuTray.IsChecked = settings.CloseToTray;
        MenuJumpList.IsChecked = settings.JumpList;
        MenuRememberSearches.IsChecked = settings.RememberSearches;
    }

    /// <summary>Ctrl+Alt+Space: the list comes forward ready to type into — or goes away when it already is in front.</summary>
    private void ToggleFromHotkey()
    {
        if (IsVisible && WindowState != WindowState.Minimized && IsActive)
        {
            if (_tray is not null) Hide();
            else WindowState = WindowState.Minimized;
            return;
        }
        ShowFromBackground();
    }

    /// <summary>Back from the tray, the taskbar or behind other windows, with the search box ready.</summary>
    public void ShowFromBackground()
    {
        if (!IsVisible) Show();
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (WindowState == WindowState.Minimized) ShowWindow(hwnd, 9 /* SW_RESTORE: maximised stays maximised */);
        Activate();
        SetForegroundWindow(hwnd);
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void ExitFromTray()
    {
        _exitRequested = true;
        Close();
    }

    /// <summary>Closes for good, tray or not: a restart is starting the next copy.</summary>
    public void ExitForRestart() => ExitFromTray();

    // ------------------------------------------------------------------ usage

    private void Usage_Click(object sender, RoutedEventArgs e) => UsageWindow.ShowFor(this, _vm, Locate);

    private async void ImportChatGptWeb_Click(object sender, RoutedEventArgs e) =>
        await ImportWebExportAsync(ConversationKey.ChatGptWeb);

    private async void ImportClaudeWeb_Click(object sender, RoutedEventArgs e) =>
        await ImportWebExportAsync(ConversationKey.ClaudeWeb);

    private async Task ImportWebExportAsync(string backend)
    {
        var (choice, files) = WebExportImportDialog.Ask(this, Infrastructure.Backends.WebExportStore.InDataRoot(backend));
        if (choice == WebExportImportDialog.Outcome.Import) await _vm.ImportWebExportAsync(backend, files);
        else if (choice == WebExportImportDialog.Outcome.Remove) await _vm.RemoveWebExportAsync(backend);
    }

    private async void ImportTakeout_Click(object sender, RoutedEventArgs e)
    {
        var (choice, files) = TakeoutImportDialog.Ask(this, new Infrastructure.Backends.GeminiTakeoutStore(LauncherPaths.GeminiTakeoutStore));
        if (choice == TakeoutImportDialog.Outcome.Import) await _vm.ImportTakeoutAsync(files);
        else if (choice == TakeoutImportDialog.Outcome.Remove) await _vm.RemoveTakeoutAsync();
    }

    private async void DshHomes_Click(object sender, RoutedEventArgs e)
    {
        if (DshHomesDialog.Ask(this, AppSettings.Current.DshHomes) is not { } homes) return;
        AppSettings.Update(s => s with { DshHomes = homes });
        await _vm.FindDeepSeekAsync();
        await _vm.SyncAsync();
    }

    /// <summary>The conversation to select once the list shows it, and until when to wait for that.</summary>
    private ConversationKey? _locate;
    private long _locateUntil;

    /// <summary>
    /// Shows one conversation in the list, asked for from another window: this window forward,
    /// every filter off, the search narrowed to its id and the row selected.
    /// </summary>
    public void Locate(SessionMetadata row)
    {
        ShowFromBackground();
        var archived = row.Visibility == ClaudeLauncher.Core.Contracts.Visibility.ArchivedView;
        if (archived != (ArchivedTab.IsChecked == true))
        {
            if (archived) ArchivedTab.IsChecked = true;
            else ActiveTab.IsChecked = true;
        }
        _vm.SelectedBackendOption = _vm.BackendOptions[0];
        _vm.SelectedTimeOption = _vm.TimeOptions[0];
        if (_vm.GroupOptions.Count > 0) _vm.SelectedGroupOption = _vm.GroupOptions[0];
        _locate = row.Key;
        _locateUntil = Environment.TickCount64 + 5000;
        _locateSearch = row.Key.NativeSessionId;
        if (SearchBox.Text != row.Key.NativeSessionId) SearchBox.Text = row.Key.NativeSessionId;
        else if (_vm.Sessions.FirstOrDefault(r => r.Metadata.Key == row.Key) is { } shown)
        {
            _locate = null;
            SessionList.SelectedItem = shown;
            SessionList.ScrollIntoView(shown);
        }
    }

    // ------------------------------------------------------- content search

    /// <summary>The menu's tick and the search box's hint say whether what was said is searched.</summary>
    private void ShowContentSearchChoice()
    {
        MenuContentSearch.IsChecked = _vm.SearchesContent;
        SearchHint.Text = _vm.SearchesContent ? "搜索标题、目录、ID 或对话内容" : "搜索标题、工作目录或会话 ID";
        ShowMineOnlyChoice();
    }

    private async void ContentSearchSetting_Click(object sender, RoutedEventArgs e)
    {
        if (MenuContentSearch.IsChecked)
        {
            AppSettings.Update(s => s with { ContentSearch = true });
            if (await _vm.EnableContentSearchAsync())
                _vm.Report("已打开对话内容搜索：正在后台建立索引，进度见状态栏右下角", StatusTone.Success);
            else
                _vm.Report("没能建立对话内容索引：数据目录无法写入", StatusTone.Warning);
        }
        else
        {
            var choice = MessageDialog.Show(this, DialogKind.Question, "关闭对话内容搜索？",
                $"会删除本机上的对话内容索引（{SessionRowViewModel.Size(MainViewModel.ContentIndexBytes())}），"
                + "它是你和助手说过的话的一份副本。\n\n标题、目录、会话 ID、名字和备注的搜索照常；"
                + "以后重新打开会再建立一次，大约半分钟。",
                primary: "关闭并删除", cancel: "取消");
            if (choice == DialogChoice.Primary)
            {
                AppSettings.Update(s => s with { ContentSearch = false });
                _vm.Report(await _vm.DisableContentSearchAsync()
                        ? "已关闭对话内容搜索，索引已删除"
                        : "已关闭对话内容搜索；索引文件暂时被占用，下次启动时删除",
                    StatusTone.Success);
            }
        }
        ShowContentSearchChoice();
    }

    private async void RebuildIndex_Click(object sender, RoutedEventArgs e)
    {
        var choice = MessageDialog.Show(this, DialogKind.Question, "清除并重建内容索引？",
            "删除本机的对话内容索引并马上重新建立，大约半分钟；这期间搜对话内容的结果不全。\n\n"
            + "只在索引出了问题——比如搜不到明明说过的话——时才需要。",
            primary: "清除并重建", cancel: "取消");
        if (choice != DialogChoice.Primary) return;
        AppSettings.Update(s => s with { ContentSearch = true });
        _vm.Report(await _vm.RebuildContentIndexAsync()
                ? "正在重新建立对话内容索引，进度见状态栏右下角"
                : "没能重新建立对话内容索引：数据目录无法写入",
            StatusTone.Info);
        ShowContentSearchChoice();
    }

    // ---------------------------------------------------------------- theme

    private void ShowThemeChoice()
    {
        var choice = AppSettings.Current.Theme;
        MenuThemeSystem.IsChecked = choice is not (Theme.Dark or Theme.Light);
        MenuThemeDark.IsChecked = choice == Theme.Dark;
        MenuThemeLight.IsChecked = choice == Theme.Light;
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string choice }) return;
        AppSettings.Update(s => s with { Theme = choice == Theme.FollowWindows ? null : choice });
        ShowThemeChoice();
        OfferThemeRestart(windowsChanged: false);
    }

    /// <summary>A theme applies at start-up: when the one wanted is not the one showing, offer to reopen.</summary>
    private void OfferThemeRestart(bool windowsChanged)
    {
        var light = Theme.WouldBeLight(AppSettings.Current.Theme);
        if (light == Theme.IsLight)
        {
            if (!windowsChanged) _vm.Report("外观已是" + (light ? "浅色" : "深色"), StatusTone.Success);
            return;
        }
        var what = light ? "浅色" : "深色";
        _vm.Report(windowsChanged
                ? $"Windows 已换成{what}模式；重新打开会话管家就会跟着换"
                : $"换成{what}需要重新打开会话管家（列表和设置都会保留）",
            StatusTone.Info, "立即重新打开", () =>
            {
                ((App)Application.Current).Restart();
                return Task.CompletedTask;
            });
    }

    /// <summary>Windows announces a change of light/dark app mode as a settings change named ImmersiveColorSet.</summary>
    private IntPtr WatchWindowsTheme(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmSettingChange = 0x001A;
        if (msg == WmSettingChange && lParam != IntPtr.Zero
            && System.Runtime.InteropServices.Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet"
            && AppSettings.Current.Theme is not (Theme.Dark or Theme.Light))
            Dispatcher.BeginInvoke(() => OfferThemeRestart(windowsChanged: true));
        return IntPtr.Zero;
    }

    private void ChangeHotkey_Click(object sender, RoutedEventArgs e)
    {
        // Let go of the one in use while choosing: the system would swallow it, and pressing it
        // again — to keep it — must reach the dialog and read as free.
        var active = _hotkey?.Name;
        _hotkey?.Dispose();
        _hotkey = null;
        var (result, name) = HotkeyDialog.Ask(this, active, automatic: AppSettings.Current.Hotkey is null);
        if (result == HotkeyDialog.Outcome.Saved)
            AppSettings.Update(s => s with { Hotkey = name, GlobalHotkey = true });
        else if (result == HotkeyDialog.Outcome.Automatic)
            AppSettings.Update(s => s with { Hotkey = null, GlobalHotkey = true });
        ApplyBackgroundSettings();
        if (result == HotkeyDialog.Outcome.Cancelled) return;
        _vm.Report(_hotkey is null
                ? "没能注册全局快捷键：都被其他程序占用了"
                : $"全局快捷键已设为 {_hotkey.Name}" + (result == HotkeyDialog.Outcome.Automatic ? "（自动选出）" : ""),
            _hotkey is null ? StatusTone.Warning : StatusTone.Success);
    }

    private void HotkeySetting_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Update(s => s with { GlobalHotkey = MenuHotkey.IsChecked });
        ApplyBackgroundSettings();
        if (_hotkey is not null) _vm.Report($"在任何地方按 {_hotkey.Name} 都能唤出会话管家，再按一次收起", StatusTone.Success);
    }

    private void TraySetting_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Update(s => s with { CloseToTray = MenuTray.IsChecked });
        ApplyBackgroundSettings();
        _vm.Report(MenuTray.IsChecked
            ? "关闭窗口后会话管家会留在右下角托盘，点图标" + (_hotkey is null ? "" : $"或按 {_hotkey.Name} ") + "回来"
            : "关闭窗口即退出会话管家", StatusTone.Success);
    }

    private void JumpListSetting_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Update(s => s with { JumpList = MenuJumpList.IsChecked });
        _ = _vm.RefreshJumpListAsync();
        _vm.Report(MenuJumpList.IsChecked
            ? "右键任务栏上的会话管家，置顶和最近的对话点一下就在 Claude / ChatGPT 里打开"
            : "任务栏右键列表里不再列出对话，只留「打开 Claude」「打开 ChatGPT」", StatusTone.Success);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    private void More_Click(object sender, RoutedEventArgs e)
    {
        MoreMenu.PlacementTarget = MoreButton;
        MoreMenu.IsOpen = true;
    }

    private void CoverageExpander_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        AppSettings.Update(s => s with { CoverageExpanded = CoverageExpander.IsExpanded });
    }
}
