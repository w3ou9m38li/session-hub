using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using ClaudeLauncher.Core.Catalog;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure;
using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Catalog;
using ClaudeLauncher.Infrastructure.Discovery;
using ClaudeLauncher.Infrastructure.Launch;
using ClaudeLauncher.Infrastructure.Search;
using ClaudeLauncher.Infrastructure.Usage;
using ViewVisibility = ClaudeLauncher.Core.Contracts.Visibility;

namespace ClaudeLauncher.App.ViewModels;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can swap its whole content with a single
/// change notification. Re-filtering a page of rows is then one layout pass for the list
/// instead of one per row, which is the difference between a flicker and none.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

/// <summary>How a status message should read: its colour and glyph in the status bar.</summary>
public enum StatusTone { Info, Busy, Success, Warning, Error }

/// <summary>What the status bar says, and the one thing it offers to do about it ("撤销", "打开文件夹").</summary>
public sealed record StatusMessage(string Text, StatusTone Tone, string? ActionLabel = null);

/// <summary>What an empty list offers instead of rows.</summary>
public enum EmptyAction { None, ClearSearch, ClearFilters, ShowActive, Sync }

/// <summary>
/// Why the list is empty and what would fill it. An empty list with no words reads as
/// "everything is gone"; this says whether that is a search, a filter, the archive view, or
/// a sync still running.
/// </summary>
public sealed record EmptyState(string Title, string Detail, string? ActionLabel, EmptyAction Action);

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>Typing pause before a search runs, so a burst of keystrokes is one query.</summary>
    private const int SearchDebounceMs = 150;

    private readonly SqliteCatalog _catalog;
    private readonly CatalogSyncService _sync;
    private readonly CodexPaths _codex = CodexPaths.Discover();

    /// <summary>
    /// One SQLite connection, several background tasks: a sync, a page read and a group write
    /// can all be in flight at once, and the connection is not safe for concurrent use.
    /// Every catalog call goes through this gate.
    /// </summary>
    private readonly SemaphoreSlim _catalogGate = new(1, 1);

    /// <summary>
    /// What was said in each conversation, so a search finds it by content. A database of its
    /// own with its own lock, brought up to date in the background after every sync; null
    /// when it could not be opened, and search then covers titles, folders and ids as before.
    /// </summary>
    private ContentIndex? _content;
    private CancellationTokenSource _indexing = new();

    /// <summary>The indexing pass on its background thread; completed when none is running.</summary>
    private Task _indexWork = Task.CompletedTask;

    /// <summary>The passes of <see cref="IndexContentAsync"/>, all of them; completed when none is running.</summary>
    private Task _indexPasses = Task.CompletedTask;
    private bool _reindex;

    private InstalledApps _apps;
    private Task<InstalledApps>? _discovery;
    private CancellationTokenSource? _reloadCts;
    private PageCursor? _nextCursor;
    private int _syncRunning;
    private bool _loadedOnce;
    private bool _loadingMore;

    /// <summary>Said once, with the first sync result, so it is not overwritten unseen.</summary>
    private string? _startupNotice;

    /// <summary>Set while the filter list is being rebuilt, so restoring the selection is not a query.</summary>
    private bool _rebuildingGroups;

    public MainViewModel()
    {
        // Runs before anything opens a file: the data root is a folder of this tool's own on
        // the data drive, and an older one under the user profile moves here.
        var migration = DataRootMigrator.PrepareDataRoot();

        // A corrupt catalog is rebuilt rather than fatal: every row is re-readable from the
        // apps' own files, so refusing to start over a derived cache would be the wrong call.
        _catalog = SqliteCatalog.OpenOrRebuild(
            LauncherPaths.CatalogDatabase, LoadSchema(), out var rebuiltFrom);
        if (AppSettings.Current.ContentSearch) _content = OpenContentIndex(_catalog);
        else DeleteContentIndexFiles();     // switched off: no copy of what was said stays behind

        _archive = OpenArchive();
        TranscriptLocator.Archive = _archive;

        RebuildNotice = RebuildNote(rebuiltFrom);
        var migrated = Describe(migration);
        _startupNotice = migrated.Length > 0 ? migrated : null;
        _status = new StatusMessage(_startupNotice ?? "正在读取会话目录…", StatusTone.Busy);

        _sync = new CatalogSyncService(_catalog, LauncherPaths.ClaudeConfigDir, GeminiPaths.Discover(),
            new GeminiTakeoutStore(LauncherPaths.GeminiTakeoutStore), LauncherPaths.ProbeCacheFile,
            () => DshPaths.Discover(AppSettings.Current.DshHomes), WebExportStore.InDataRoot());
        _apps = new InstalledApps(
            new ClaudeInstallation { Found = false, FailureReason = "尚未探测" },
            new ChatGptInstallation { Found = false, FailureReason = "尚未探测" });
        Sessions = [];
        CoverageItems = [];
        GroupOptions = [];
        _selectedGroupOption = new GroupOptionViewModel(GroupFilter.All, "全部分组");
        GroupOptions.Add(_selectedGroupOption);
        _selectedBackendOption = BackendOptionViewModel.All[0];
        CoverageItems.Add(new CoverageItemViewModel("尚未同步",
            "首次同步在后台进行，列表先显示上次的目录；同步完成后这里会列出每个来源的读取结果。",
            CoverageTone.Neutral));
        _empty = new EmptyState("正在读取会话目录…", "", null, EmptyAction.None);
    }

    /// <summary>The index of what was said, attached to the catalog's searches; null when it cannot be had.</summary>
    private static ContentIndex? OpenContentIndex(SqliteCatalog catalog)
    {
        if (ContentIndex.OpenOrRebuild(LauncherPaths.ContentIndexDatabase) is not { } index) return null;
        try
        {
            catalog.AttachContentIndex(index.DatabasePath);
            return index;
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            index.Dispose();
            return null;
        }
    }

    private static string? RebuildNote(string? rebuiltFrom) => rebuiltFrom is null
        ? null
        : $"目录数据库曾损坏，已重建。原文件保留在 {rebuiltFrom}。\n\n本工具自己的归档与分组偏好已丢失；"
          + "会话本身都在 Claude / ChatGPT 的文件里，未受影响，同步后会重新列出。";

    private static string Describe(MigrationReport migration) => migration.Outcome switch
    {
        MigrationOutcome.Moved =>
            $"数据目录已迁移到 {migration.To}（{migration.Files} 个文件），旧目录已删除",
        MigrationOutcome.CopiedOldKept =>
            $"数据目录已复制到 {migration.To}（{migration.Files} 个文件），但旧目录未能删除：{migration.Message}",
        MigrationOutcome.Failed =>
            $"数据目录迁移未完成，仍在使用原目录：{migration.Message}",
        _ => migration.Message ?? string.Empty,
    };

    public BulkObservableCollection<SessionRowViewModel> Sessions { get; }
    public BulkObservableCollection<CoverageItemViewModel> CoverageItems { get; }
    public BulkObservableCollection<GroupOptionViewModel> GroupOptions { get; }
    public IReadOnlyList<BackendOptionViewModel> BackendOptions => BackendOptionViewModel.All;

    /// <summary>
    /// Raised right before the list is replaced, so the view can remember its selection.
    /// True when the replacement is a refresh of the same view (after a sync or an organising
    /// action) and the view should also keep its scroll position.
    /// </summary>
    public event Action<bool>? ListReplacing;

    /// <summary>Shown once as a dialog: the catalog had to be rebuilt and the user's own grouping went with it.</summary>
    public string? RebuildNotice { get; }

    // --------------------------------------------------------------- status

    private StatusMessage _status;
    private Func<Task>? _statusAction;

    public StatusMessage Status => _status;

    /// <summary>
    /// Puts a message in the status bar. <paramref name="action"/> is offered as a link next to
    /// it — the natural next step (undo, open the folder) one click away instead of a hunt.
    /// </summary>
    public void Report(string text, StatusTone tone = StatusTone.Info, string? actionLabel = null, Func<Task>? action = null)
    {
        _statusAction = action;
        _status = new StatusMessage(text, tone, action is null ? null : actionLabel);
        OnPropertyChanged(nameof(Status));
    }

    public Task RunStatusActionAsync() => _statusAction?.Invoke() ?? Task.CompletedTask;

    private string _installationText = "正在探测 Claude 与 ChatGPT…";
    public string InstallationText { get => _installationText; private set => Set(ref _installationText, value); }

    public string DataRoot => LauncherPaths.DataRoot;

    private string _listSummary = string.Empty;
    public string ListSummary { get => _listSummary; private set => Set(ref _listSummary, value); }

    private bool _isSyncing;
    public bool IsSyncing { get => _isSyncing; private set => Set(ref _isSyncing, value); }

    private string _lastSyncText = "尚未同步";
    public string LastSyncText { get => _lastSyncText; private set => Set(ref _lastSyncText, value); }

    private int _totalCount;
    /// <summary>Every row the current view, app, group and search reach — not just the pages loaded.</summary>
    public int TotalCount { get => _totalCount; private set => Set(ref _totalCount, value); }

    private ViewCounts _views = new(0, 0);
    public ViewCounts Views { get => _views; private set => Set(ref _views, value); }

    private EmptyState? _empty;
    /// <summary>Null while there are rows to show.</summary>
    public EmptyState? Empty { get => _empty; private set => Set(ref _empty, value); }

    private string _coverageSummary = "尚未同步";
    public string CoverageSummary { get => _coverageSummary; private set => Set(ref _coverageSummary, value); }

    private CoverageTone _coverageTone = CoverageTone.Neutral;
    public CoverageTone CoverageSummaryTone { get => _coverageTone; private set => Set(ref _coverageTone, value); }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            var wasSearching = IsSearching;
            if (!Set(ref _searchText, value ?? string.Empty)) return;
            // Search results keep an order of their own: starting or ending a search changes the order shown.
            if (IsSearching != wasSearching)
            {
                OnPropertyChanged(nameof(IsSearching));
                OnPropertyChanged(nameof(Sort));
            }
            _ = ReloadAsync(debounce: true);
        }
    }

    /// <summary>Something is typed in the search box.</summary>
    public bool IsSearching => !string.IsNullOrWhiteSpace(_searchText);

    private bool _searchMineOnly;
    /// <summary>What was said counts only when the user said it — the answers are left out.</summary>
    public bool SearchMineOnly
    {
        get => _searchMineOnly;
        set { if (Set(ref _searchMineOnly, value) && IsSearching && _content is not null) _ = ReloadAsync(); }
    }

    private bool _showArchivedView;
    public bool ShowArchivedView
    {
        get => _showArchivedView;
        set { if (Set(ref _showArchivedView, value)) _ = RefreshViewAsync(); }
    }

    private GroupOptionViewModel _selectedGroupOption;
    public GroupOptionViewModel SelectedGroupOption
    {
        get => _selectedGroupOption;
        set
        {
            // The ComboBox clears its selection while the list is swapped; that is not a
            // filter change and must not run a query or lose the user's choice.
            if (value is null || _rebuildingGroups) return;
            if (Set(ref _selectedGroupOption, value)) _ = ReloadAsync();
        }
    }

    private BackendOptionViewModel _selectedBackendOption;
    public BackendOptionViewModel SelectedBackendOption
    {
        get => _selectedBackendOption;
        set
        {
            if (value is null) return;
            // Group counts are per app, so the group list is rebuilt with the rows.
            if (Set(ref _selectedBackendOption, value)) _ = RefreshViewAsync();
        }
    }

    public bool HasMorePages => _nextCursor is not null;

    public bool IsFiltered =>
        !string.IsNullOrWhiteSpace(SearchText)
        || _selectedGroupOption.Filter.Kind != GroupFilterKind.All
        || !CurrentBackend.IsAll
        || _selectedTimeOption.Window != TimeWindow.All;

    private TimeOptionViewModel _selectedTimeOption = TimeOptionViewModel.All[0];
    public IReadOnlyList<TimeOptionViewModel> TimeOptions => TimeOptionViewModel.All;

    /// <summary>How far back the list reaches. Search and filters apply within it.</summary>
    public TimeOptionViewModel SelectedTimeOption
    {
        get => _selectedTimeOption;
        set
        {
            if (value is null) return;
            if (Set(ref _selectedTimeOption, value)) _ = ReloadAsync();
        }
    }

    /// <summary>Starts with the time limit the user left last time, without a query of its own.</summary>
    public void UseTimeFilter(string? window)
    {
        if (TimeOptionViewModel.All.FirstOrDefault(o => o.Window.ToString() == window) is not { } option) return;
        _selectedTimeOption = option;
        OnPropertyChanged(nameof(SelectedTimeOption));
    }

    private DateTimeOffset? CurrentSince => _selectedTimeOption.Since(DateTime.Today);

    private ViewVisibility CurrentVisibility =>
        ShowArchivedView ? ViewVisibility.ArchivedView : ViewVisibility.Active;

    private BackendFilter CurrentBackend => _selectedBackendOption.Filter;

    /// <summary>
    /// Starts in the app filter the user left last time. Set without a query of its own:
    /// the first load, which runs anyway, uses it.
    /// </summary>
    public void UseBackendFilter(string? kind)
    {
        if (BackendOptionViewModel.All.FirstOrDefault(o => o.Filter.Name == kind) is not { } option) return;
        _selectedBackendOption = option;
        OnPropertyChanged(nameof(SelectedBackendOption));
    }

    /// <summary>Back to the whole view: no search, every app, every group.</summary>
    public void ClearFilters()
    {
        SearchText = string.Empty;
        SelectedBackendOption = BackendOptionViewModel.All[0];
        SelectedTimeOption = TimeOptionViewModel.All[0];
        if (GroupOptions.Count > 0) SelectedGroupOption = GroupOptions[0];
    }

    // --------------------------------------------------------------- launch

    /// <summary>
    /// Opening an app never waits for indexing — discovery and activation are the only work
    /// on this path, so a slow or failing sync can never block the user's main goal. Both run
    /// off the UI thread, so the window keeps responding while Windows answers.
    /// </summary>
    public async Task<LaunchResult> OpenClaudeAsync()
    {
        var apps = await AppsAsync();
        if (!apps.Claude.Found) apps = await RediscoverAsync();
        var result = await Task.Run(() => WindowsClaudeLauncher.Activate(apps.Claude));
        Report(result.Message, Launched(result) ? StatusTone.Success : StatusTone.Error);
        return result;
    }

    public async Task<LaunchResult> OpenChatGptAsync()
    {
        var apps = await AppsAsync();
        if (!apps.ChatGpt.Found && apps.ChatGpt.ManagedShortcut is null) apps = await RediscoverAsync();
        var result = await Task.Run(() => ChatGptLauncher.Open(apps.ChatGpt));
        Report(result.Message, Launched(result) ? StatusTone.Success : StatusTone.Error);
        return result;
    }

    /// <summary>
    /// The user's own entry point to DeepSeek Harness — its shortcut, or the start script beside a
    /// home — when there is one; null hides 「打开 DeepSeek」. Looked for again when the homes change.
    /// </summary>
    public string? DeepSeekLaunchTarget
    {
        get => _deepSeekLaunchTarget;
        private set
        {
            if (_deepSeekLaunchTarget == value) return;
            _deepSeekLaunchTarget = value;
            SessionRowViewModel.CanStartApp = source => source.Family == AppFamily.DeepSeek && value is not null;
            OnPropertyChanged();
        }
    }
    private string? _deepSeekLaunchTarget;

    public async Task FindDeepSeekAsync()
    {
        var homes = AppSettings.Current.DshHomes;
        DeepSeekLaunchTarget = await Task.Run(() => DeepSeekHarnessLauncher.Find(DshPaths.Discover(homes).Homes));
    }

    /// <summary>Starts DeepSeek Harness through the user's own entry point: it opens in the browser, signed in.</summary>
    public async Task<LaunchResult> OpenDeepSeekAsync()
    {
        if (DeepSeekLaunchTarget is null || !File.Exists(DeepSeekLaunchTarget)) await FindDeepSeekAsync();
        var target = DeepSeekLaunchTarget;
        var result = await Task.Run(() => DeepSeekHarnessLauncher.Open(target));
        Report(result.Message, Launched(result) ? StatusTone.Success : StatusTone.Error);
        return result;
    }

    private static bool Launched(LaunchResult result) =>
        result.Outcome is LaunchOutcome.ActivationRequested or LaunchOutcome.AlreadyRunning;

    /// <summary>The discovery started at launch, waited for only if it is still running.</summary>
    private async Task<InstalledApps> AppsAsync()
    {
        if (_discovery is { IsCompleted: false } pending)
        {
            Report("正在定位已安装的 Claude 与 ChatGPT…", StatusTone.Busy);
            _apps = await pending;
        }
        return _apps;
    }

    /// <summary>One more look before reporting "not found": the app may have been installed since the window opened.</summary>
    private async Task<InstalledApps> RediscoverAsync()
    {
        Report("正在重新查找已安装的应用…", StatusTone.Busy);
        _apps = await Task.Run(() => WindowsAppDiscovery.Discover());
        UpdateInstallationText();
        return _apps;
    }

    /// <summary>
    /// Opens the conversation inside its own app, through the app's own link: claude:// for
    /// Claude, codex://threads/… for a Codex task in ChatGPT — handed to the copy of ChatGPT
    /// that is running (the Codex++ one included), or started first. Launching is all either
    /// route does.
    /// </summary>
    public async Task<DeepLinkResult> OpenInAppAsync(SessionRowViewModel row)
    {
        if (row.Source.Open == OpenRoute.AppOnly)
        {
            // No link reaches one conversation: the app is opened, and where to find it said.
            if (row.Source.Family != AppFamily.DeepSeek)
                return new DeepLinkResult(DeepLinkOutcome.UnsupportedBackend, row.OpenInAppTooltip);
            var started = await OpenDeepSeekAsync();
            if (!Launched(started))
                return new DeepLinkResult(DeepLinkOutcome.Failed, started.Message);
            var where = row.Metadata.ProjectRef is { Length: > 0 } folder ? $"工作目录 {folder} 下的" : "";
            var message = $"已请求打开 {row.AppName}：在它的侧边栏里找{where}「{row.Metadata.Title}」";
            Report(message, StatusTone.Success);
            return new DeepLinkResult(DeepLinkOutcome.Requested, message);
        }

        if (row.Source.Open == OpenRoute.None)
            return new DeepLinkResult(DeepLinkOutcome.UnsupportedBackend, row.OpenInAppTooltip);

        if (row.Source.Open == OpenRoute.WebLink)
        {
            var shown = WebLinks.Open(row.Metadata.Key.Backend, row.Metadata.Key.NativeSessionId);
            Report(shown.Outcome == DeepLinkOutcome.Requested ? $"{shown.Message}：「{row.Metadata.Title}」" : shown.Message,
                shown.Outcome == DeepLinkOutcome.Requested ? StatusTone.Success : StatusTone.Error);
            return shown;
        }

        var apps = await AppsAsync();
        if (row.Source.Open == OpenRoute.CodexLink)
        {
            if (!ChatGptDeepLink.CanOpen(row.Metadata.Key.NativeSessionId))
            {
                Report("这条任务的 ID 不是 ChatGPT 能打开的格式", StatusTone.Error);
                return new DeepLinkResult(DeepLinkOutcome.InvalidSessionId, "任务 ID 格式不合法");
            }
            Report("正在请求 ChatGPT 打开这条任务…", StatusTone.Busy);
            var opened = await Task.Run(() => ChatGptDeepLink.OpenAsync(apps.ChatGpt, row.Metadata.Key.NativeSessionId));
            Report(opened.Outcome == DeepLinkOutcome.Requested
                    ? $"{opened.Message}：「{row.Metadata.Title}」"
                      + (row.IsArchivedInApp ? "（它在 ChatGPT 里已归档，ChatGPT 会先问是否取消归档）" : "")
                    : opened.Message,
                opened.Outcome == DeepLinkOutcome.Requested ? StatusTone.Success : StatusTone.Error);
            return opened;
        }

        // The earlier part of a continued desktop session is that session in Claude: opened as
        // it, never imported a second time as a conversation of its own.
        var result = row.Metadata.ContinuedIn is { } desktop
            ? await Task.Run(() => ClaudeDeepLink.Open(ConversationKey.DesktopCode, desktop, null))
            : await Task.Run(() => ClaudeDeepLink.Open(
                row.Metadata.Key.Backend,
                row.Metadata.Key.Backend == ConversationKey.DesktopCode ? row.Metadata.Key.NativeSessionId : null,
                row.CliSessionId));

        // Handing the link to the shell is not proof the app opened it, so the wording stays
        // at "requested".
        if (result.Outcome == DeepLinkOutcome.Requested)
            Report(row.IsEarlierPart ? "已请求 Claude 打开这份记录所属的桌面会话（这一份是它续接前的部分）" : result.Message,
                StatusTone.Success);
        else
            Report(result.Message, StatusTone.Error);
        return result;
    }

    /// <summary>
    /// A Claude conversation's other parts: the desktop session chain it belongs to, in the
    /// order it happened, and its resumes. Nothing for a Codex task.
    /// </summary>
    public Task<Relatives> RelativesAsync(SessionRowViewModel row) =>
        row.IsClaude || row.IsCodex
            ? OnCatalog(() => new Relatives(
                row.HasEarlierParts || row.IsEarlierPart ? _catalog.ContinuationChain(row.Metadata) : [],
                _catalog.Resumes(row.Metadata)))
            : Task.FromResult(Relatives.None);

    /// <summary>
    /// Continues a conversation with its own CLI: <c>claude --resume</c> for a Claude row,
    /// <c>codex resume</c> for a Codex thread. Both only launch; the app does the rest.
    /// </summary>
    public async Task<ResumeResult> ResumeCliAsync(SessionRowViewModel row)
    {
        var apps = await AppsAsync();
        var cwd = row.Metadata.ProjectRef ?? string.Empty;
        Report("正在打开终端…", StatusTone.Busy);
        var result = await Task.Run(() => row.Source.Resume switch
        {
            ResumeRoute.CodexCli => CodexSessionLauncher.Resume(row.Metadata.Key.NativeSessionId, cwd,
                managedAppRoot: apps.ChatGpt.ManagedAppRoot, storeInstallLocation: apps.ChatGpt.InstallLocation),
            ResumeRoute.GeminiCli => GeminiSessionLauncher.Resume(row.Metadata.Key.NativeSessionId, cwd),
            ResumeRoute.ClaudeCli => CliSessionLauncher.Resume(row.CliSessionId, cwd, profileRoot: apps.Claude.ProfileRoot),
            _ => new ResumeResult(ResumeOutcome.Failed, row.ResumeTooltip),
        });
        Report(result.Message, result.Outcome == ResumeOutcome.Started ? StatusTone.Success : StatusTone.Error);
        return result;
    }

    // ------------------------------------------------------------- catalog

    public async Task InitializeAsync()
    {
        // Discovery asks PowerShell and can take a second or two; the cached catalog does not
        // depend on it, so the list is shown first and discovery finishes behind it.
        _discovery = Task.Run(() => WindowsAppDiscovery.Discover());
        _ = FindDeepSeekAsync();
        await RefreshViewAsync();

        _apps = await _discovery;
        UpdateInstallationText();
        _ = SyncAsync();          // background; the list is usable immediately
    }

    private void UpdateInstallationText()
    {
        var claude = !_apps.Claude.Found ? "Claude 未发现"
            : _apps.Claude.Flavor == ClaudeFlavor.Standalone ? $"Claude {_apps.Claude.Version}（独立安装版）"
            : $"Claude {_apps.Claude.Version}";
        var chatgpt = _apps.ChatGpt.Found
            ? $"ChatGPT {_apps.ChatGpt.Version}" + (_apps.ChatGpt.ManagedShortcut is null ? "" : "（经 Codex++ 快捷方式启动）")
            : _apps.ChatGpt.ManagedShortcut is null ? "ChatGPT 未发现" : "ChatGPT（仅 Codex++ 快捷方式）";
        InstallationText = $"{claude}　·　{chatgpt}";
    }

    /// <summary>When the last sync finished, whether or not it succeeded; null until the first one has.</summary>
    public DateTime? LastSyncedUtc { get; private set; }

    /// <summary>The running sync has read the sources: a sync asked for now must read them again.</summary>
    private bool _scanned;
    private bool _rescan;

    /// <summary>True while the coverage report is being replaced by an automatic sync: the view keeps its place.</summary>
    public bool CoverageRefreshIsQuiet { get; private set; }

    /// <summary>
    /// Asked by the view before an automatic refresh reorders the list: true while the user
    /// is in the middle of something — pointing at a row, typing, a menu open.
    /// </summary>
    public Func<bool>? IsUserBusy { get; set; }

    /// <summary>Set when someone asked to see a sync while a quiet one was already running.</summary>
    private bool _reportRunningSync;

    /// <summary>
    /// Re-reads the apps' records. A <paramref name="quiet"/> sync is the automatic kind —
    /// on coming back to the window, and once a minute while it is on screen: it shows no
    /// progress, never replaces a status message (which may be offering 撤销), says nothing
    /// when it fails, and touches the list only when something changed — after the user has
    /// paused, so rows never move under a pointer about to click.
    /// </summary>
    public async Task SyncAsync(bool quiet = false)
    {
        if (Interlocked.Exchange(ref _syncRunning, 1) == 1)
        {
            // Already running. A quiet request has nothing to add; a visible one is answered,
            // and the running sync reports its result as if it had been asked for.
            if (!quiet)
            {
                _reportRunningSync = true;
                // Asked for after the running sync read the sources: it reads them once more,
                // so the answer is never older than the request.
                if (_scanned) _rescan = true;
                IsSyncing = true;
                Report("正在同步，请稍候…", StatusTone.Busy);
            }
            return;
        }

        _reportRunningSync = !quiet;
        if (!quiet)
        {
            IsSyncing = true;
            UpdateEmptyState();
            Report("正在同步：扫描 Claude 与 ChatGPT 的本地记录…", StatusTone.Busy);
        }
        try
        {
            SyncResult result;
            int changed = 0, removed = 0;
            await FirstLookAsync();
            do
            {
                _rescan = false;
                _scanned = false;
                var apps = await AppsAsync();
                // The apps' files are read with the list free to page and search; only the
                // writing — milliseconds — waits for the catalog.
                var codex = _codex;
                var read = await Task.Run(() => _sync.Read(apps.Claude, codex));
                result = await OnCatalog(() => _sync.Apply(read));
                _scanned = true;
                changed += result.RowsChanged;
                removed += result.RowsRemoved;
                CoverageRefreshIsQuiet = !_reportRunningSync;
                ShowCoverage(result);
                CoverageRefreshIsQuiet = false;
                LastSyncText = $"上次同步 {DateTime.Now:HH:mm}";

                // F5 during the wait ends it: then the user is asking for the result now.
                if (result.ChangedAnything && !_reportRunningSync) await WhenUserIsIdleAsync();
                if (result.ChangedAnything || _reportRunningSync) await RefreshViewAsync(keepDepth: true);
            } while (_rescan);

            // Said last, after the list shows it, and with nothing awaited in between — so a
            // request arriving up to this point is answered by this result, never left hanging
            // on 「正在同步，请稍候…」.
            if (_reportRunningSync)
            {
                var text = result.Warning ?? DescribeSync(result with { RowsChanged = changed, RowsRemoved = removed });
                if (_startupNotice is { } notice) text += "　·　" + notice;
                _startupNotice = null;
                Report(text, result.Warning is null ? StatusTone.Success : StatusTone.Warning);
            }
        }
        catch (Exception ex)
        {
            if (_reportRunningSync) Report("同步失败，已保留原有目录：" + ex.Message, StatusTone.Error);
        }
        finally
        {
            // A failed sync counts too: otherwise one failure at start-up would switch the
            // automatic refresh off until someone synced by hand.
            LastSyncedUtc = DateTime.UtcNow;
            CoverageRefreshIsQuiet = false;
            IsSyncing = false;
            _reportRunningSync = false;
            _scanned = false;
            Interlocked.Exchange(ref _syncRunning, 0);
            UpdateEmptyState();
        }
        _ = RunBackgroundPassesAsync();
    }

    /// <summary>Terminal transcripts read first on a first launch, the ones written last.</summary>
    private const int FirstLookTranscripts = 300;

    private bool _firstLookDone;

    /// <summary>
    /// A first launch on a long history: before the whole of it is read — thousands of
    /// transcripts take a while the first time — the conversations written last are read and
    /// listed, so the window has something to show within a second or two. The full read
    /// follows straight after, as usual.
    /// </summary>
    private async Task FirstLookAsync()
    {
        if (_firstLookDone) return;
        _firstLookDone = true;
        try
        {
            if (!await OnCatalog(() => _catalog.IsEmpty())) return;
            var apps = await AppsAsync();
            var codex = _codex;
            var first = await Task.Run(() => _sync.Read(apps.Claude, codex, newestTranscripts: FirstLookTranscripts));
            await OnCatalog(() => _sync.Apply(first));
            await RefreshViewAsync();
            if (_reportRunningSync)
                Report($"先列出了最近的 {Sessions.Count} 条对话，其余的正在读取（第一次打开要多花一点时间）…", StatusTone.Busy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            // The full read that follows says what went wrong.
        }
    }

    private bool _passesRunning;
    private bool _passesAgain;

    /// <summary>
    /// What a sync sets going: the content index brought up to date, then the usage ledger, then
    /// this tool's copies — one after the other. Side by side they took two processors and the
    /// memory of both at once, and the window stuttered for as long as they ran. The copies come
    /// last: their pass may take its whole twenty seconds, and the figures should not wait for
    /// it. Memory a long pass went through is handed back to the system once the user pauses.
    /// </summary>
    private async Task RunBackgroundPassesAsync()
    {
        if (_passesRunning)
        {
            _passesAgain = true;        // this sync may have found more: once more after the running ones
            return;
        }
        _passesRunning = true;
        try
        {
            do
            {
                _passesAgain = false;
                await IndexContentAsync();
                await RefreshUsageQuietlyAsync();
                await KeepCopiesAsync();

                if (MemoryRelief.IsDue && !_passesAgain && !_disposed)
                {
                    // A pass's thread lets go of the buffers it kept for itself only as it ends: a moment for that first.
                    await Task.Delay(2000);
                    await WhenUserPausedAsync();
                    await MemoryRelief.ReleaseAsync();
                }
            } while (_passesAgain && !_disposed);
        }
        finally
        {
            _passesRunning = false;
        }
    }

    // ---------------------------------------------------------------- copies

    /// <summary>This tool's own copies of the transcripts; null when its folder cannot be opened.</summary>
    private readonly TranscriptArchive? _archive;

    private static TranscriptArchive? OpenArchive()
    {
        try
        {
            return new TranscriptArchive(LauncherPaths.ArchiveDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            return null;        // the list works as before; nothing is kept beyond what the apps keep
        }
    }

    /// <summary>
    /// After a sync, this tool's copy of every listed transcript is brought up to date: the
    /// first time all of them, twenty seconds at a time and oldest first (the first an app
    /// cleans up); after that only what grew. Time to spare packs copies from before copies were
    /// packed. A conversation whose app later deletes its record stays readable here.
    /// </summary>
    private async Task KeepCopiesAsync()
    {
        if (_archive is not { } archive || _disposed) return;
        try
        {
            var rows = await OnCatalog(() => _catalog.ListedRows());
            var token = _usageStop.Token;
            var progress = new ThrottledProgress<(int Done, int Total)>(p =>
                IndexProgress = p.Total >= 20 && p.Done < p.Total ? $"正在给对话记录做副本 {p.Done}/{p.Total}" : null,
                p => p.Done >= p.Total);
            var codex = _codex;
            await OnBackgroundThread(() =>
            {
                var own = archive.CopiesFolder + Path.DirectorySeparatorChar;
                var sources = TranscriptLocator.FindAll(rows, LauncherPaths.ClaudeConfigDir, codex, token)
                    .Where(f => !f.Path.StartsWith(own, StringComparison.OrdinalIgnoreCase))
                    .Select(f => TranscriptArchive.IdentityOf(f.Row) is { } id
                        ? new TranscriptArchive.Source(f.Path, id.Family, id.Conversation) : null)
                    .OfType<TranscriptArchive.Source>()
                    .ToList();
                return archive.Update(sources, TimeSpan.FromSeconds(20), progress, token);
            });
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException
                                       or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            // The next sync carries on where this one stopped.
        }
        finally
        {
            IndexProgress = null;
        }
    }

    /// <summary>
    /// Deletes conversations from this tool at the user's word: the rows, this tool's copies of
    /// their transcripts, and (at the next index pass) what the index holds of them. A sync
    /// never lists them again. The apps' own records are not touched.
    /// </summary>
    public async Task DeleteAsync(IReadOnlyList<SessionRowViewModel> rows)
    {
        if (rows.Count == 0) return;
        try
        {
            var deleted = await OnCatalog(() => _catalog.Delete(rows.Select(r => r.Metadata.Key)));
            if (_archive is { } archive)
                await Task.Run(() =>
                {
                    foreach (var row in rows)
                        if (TranscriptArchive.IdentityOf(row.Metadata) is { } id) archive.Remove(id.Family, id.Conversation);
                });
            await RefreshViewAsync(keepDepth: true);
            Report($"已从会话管家删除 {rows.Count} 条对话" + (deleted.Count > rows.Count ? $"（连同并入它们的 {deleted.Count - rows.Count} 份记录）" : "")
                   + "；应用自己的记录没有改动", StatusTone.Success);
            _ = RunBackgroundPassesAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Report("删除失败：" + ex.Message, StatusTone.Error);
        }
    }

    // -------------------------------------------------------- content index

    private string? _indexProgress;

    /// <summary>「正在建立对话内容索引 120/791」 while a long indexing pass runs; null otherwise.</summary>
    public string? IndexProgress
    {
        get => _indexProgress;
        private set
        {
            if (!Set(ref _indexProgress, value)) return;
            UpdateListSummary();
            UpdateEmptyState();
        }
    }

    /// <summary>Whether a search also looks at what was said.</summary>
    public bool SearchesContent => _content is not null;

    /// <summary>
    /// Brings the index of what was said up to date with the catalog, at low priority. The
    /// first pass reads every transcript once — about half a minute for gigabytes of them;
    /// after that only what grew is read, in a fraction of a second. A search on screen is
    /// refreshed as the index learns, so results fill in while the first pass runs. Asked while
    /// a pass runs, it waits for that one and for one more after it.
    /// </summary>
    private Task IndexContentAsync()
    {
        if (_content is null || _disposed) return Task.CompletedTask;
        if (!_indexPasses.IsCompleted)
        {
            _reindex = true;        // the running pass may predate this sync: once more after it
            return _indexPasses;
        }
        return _indexPasses = IndexContentPassesAsync();
    }

    private async Task IndexContentPassesAsync()
    {
        if (_content is not { } index) return;
        try
        {
            do
            {
                _reindex = false;
                var rows = await OnCatalog(() => _catalog.ListedRows());
                var cancel = _indexing.Token;
                var progress = new ThrottledProgress<(int Done, int Total)>(ShowIndexProgress, p => p.Done >= p.Total);
                var work = OnBackgroundThread(() => index.Update(
                    TranscriptLocator.FindAll(rows, LauncherPaths.ClaudeConfigDir, _codex, cancel)
                        // A copy: only what was said in it; the copied part is the original's.
                        .Select(f => new ContentIndex.Source(ContentIndex.KeyOf(f.Row.Key), f.Path, OwnPart.For(f.Row)))
                        .ToList(),
                    progress, cancel,
                    // Still listed with its file gone: what was said stays searchable.
                    rows.Select(r => ContentIndex.KeyOf(r.Key)).ToHashSet(StringComparer.Ordinal)));
                _indexWork = work;
                var run = await work;
                if (run.ChangedAnything) await ContentChangedAsync();
            } while (_reindex && !_disposed);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException
                                       or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            // A convenience, not the catalog: search keeps working on titles, and the next sync tries again.
        }
        finally
        {
            IndexProgress = null;
        }
    }

    /// <summary>What the index takes on disk, for the question before it is deleted.</summary>
    public static long ContentIndexBytes()
    {
        long total = 0;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                var file = new FileInfo(LauncherPaths.ContentIndexDatabase + suffix);
                if (file.Exists) total += file.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* not counted */ }
        }
        return total;
    }

    /// <summary>Deletes the index's files; false when one is held open and stays for now.</summary>
    private static bool DeleteContentIndexFiles()
    {
        var deleted = true;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                var path = LauncherPaths.ContentIndexDatabase + suffix;
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                deleted = false;
            }
        }
        return deleted;
    }

    /// <summary>
    /// Content search off: indexing stops, searches stop consulting the index, and its files are
    /// deleted — a copy of what was said is not kept once it is not wanted. False when a file
    /// could not be deleted yet (it goes at the next start).
    /// </summary>
    public async Task<bool> DisableContentSearchAsync()
    {
        if (_content is { } index)
        {
            _indexing.Cancel();
            // The pass stops at its next transcript (or within a few hundred messages of one).
            await _indexWork.ContinueWith(static _ => { }, CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
            await OnCatalog(() =>
            {
                _catalog.DetachContentIndex();
                return true;
            });
            _content = null;
            index.Dispose();
            _indexing.Dispose();
            _indexing = new CancellationTokenSource();
            IndexProgress = null;
            OnPropertyChanged(nameof(SearchesContent));
            // Rows found only by what was said leave the list.
            if (!string.IsNullOrWhiteSpace(SearchText)) await ReloadAsync(keepDepth: true);
        }
        return DeleteContentIndexFiles();
    }

    /// <summary>Content search on: the index is opened (or made) and brought up to date in the background.</summary>
    public async Task<bool> EnableContentSearchAsync()
    {
        if (_content is not null) return true;
        var index = await OnCatalog(() => OpenContentIndex(_catalog));
        if (index is null) return false;
        _content = index;
        OnPropertyChanged(nameof(SearchesContent));
        _ = IndexContentAsync();
        return true;
    }

    /// <summary>Starts the index over: deleted, then built again from the transcripts.</summary>
    public async Task<bool> RebuildContentIndexAsync()
    {
        await DisableContentSearchAsync();
        return await EnableContentSearchAsync();
    }

    /// <summary>Shown only for a pass long enough to notice; every hundred transcripts a search on screen fills in.</summary>
    private void ShowIndexProgress((int Done, int Total) p)
    {
        IndexProgress = p.Total >= 20 && p.Done < p.Total ? $"正在建立对话内容索引 {p.Done}/{p.Total}" : null;
        if (p.Done > 0 && p.Done % 100 == 0 && p.Done < p.Total && !string.IsNullOrWhiteSpace(SearchText))
            _ = ContentChangedAsync();
    }

    /// <summary>The index learned something: searches look it up again, and one on screen is redone.</summary>
    private async Task ContentChangedAsync()
    {
        await OnCatalog(() =>
        {
            _catalog.ContentIndexChanged();
            return true;
        });
        if (string.IsNullOrWhiteSpace(SearchText)) return;
        // Never under a pointer about to click: rows found by content can move the list.
        await WhenUserPausedAsync();
        if (!_disposed) await ReloadAsync(keepDepth: true);
    }

    /// <summary>Where the term that found this row was said in it, for the side panel; empty when none.</summary>
    public async Task<IReadOnlyList<ContentHit>> ContentHitsAsync(SessionRowViewModel row, CancellationToken cancellation)
    {
        if (_content is not { } index || row.ContentTerm is not { } term) return [];
        try
        {
            return await Task.Run(
                () => index.Hits(ContentIndex.KeyOf(row.Metadata.Key), term, max: 3, userOnly: row.ContentByUser), cancellation);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException
                                       or Microsoft.Data.Sqlite.SqliteException)
        {
            return [];
        }
    }

    // ------------------------------------------------------------------ usage

    private UsageLedger? _usage;
    private readonly SemaphoreSlim _usageGate = new(1, 1);

    /// <summary>Stops a usage read when the program closes, before the ledger it writes goes away.</summary>
    private readonly CancellationTokenSource _usageStop = new();

    /// <summary>When the ledger was last brought up to date in this run; null before.</summary>
    public DateTime? UsageUpdatedAt { get; private set; }

    /// <summary>The ledger learned something: a selected conversation's usage line is read again.</summary>
    public event Action? UsageChanged;

    /// <summary>
    /// After a sync, a ledger the user has started keeps up by itself, quietly, the way the
    /// content index does: only what grew since is read. Nothing happens for someone who has
    /// never opened the usage window — there is no ledger to keep.
    /// </summary>
    private async Task RefreshUsageQuietlyAsync()
    {
        if (_disposed || !File.Exists(LauncherPaths.UsageDatabase)) return;
        if (!await _usageGate.WaitAsync(0)) return;       // the usage window is reading it right now
        try
        {
            var token = _usageStop.Token;
            var rows = await OnCatalog(() => _catalog.ListedRows());
            if (_usage is null)
            {
                var opened = await Task.Run(() => UsageLedger.OpenOrRebuild(LauncherPaths.UsageDatabase), token);
                if (_disposed)
                {
                    opened?.Dispose();
                    return;
                }
                _usage = opened;
            }
            if (_usage is not { } ledger) return;
            var codex = _codex;
            await OnBackgroundThread(() => ledger.Update(
                ledger.SourcesFor(rows, LauncherPaths.ClaudeConfigDir, codex, token), null, token));
            UsageUpdatedAt = DateTime.Now;
            UsageChanged?.Invoke();
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException
                                       or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            // A convenience: the usage window reads everything again when opened.
        }
        finally
        {
            _usageGate.Release();
        }
    }

    /// <summary>One conversation's tokens as the ledger last counted them; null without a ledger or any count.</summary>
    public async Task<TokenCounts?> UsageOfAsync(SessionRowViewModel row)
    {
        if (_usage is not { } ledger) return null;
        try
        {
            return await Task.Run(() => ledger.Totals(ContentIndex.KeyOf(row.Metadata.Key)));
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException
                                       or Microsoft.Data.Sqlite.SqliteException)
        {
            return null;
        }
    }

    /// <summary>The tokens counted, with the listed conversations to name them by.</summary>
    public sealed record UsageReport(IReadOnlyList<SessionMetadata> Rows, IReadOnlyList<UsageRow> Usage, int Files);

    /// <summary>
    /// Brings the usage ledger up to date and reads it. Only the usage window asks: nothing is
    /// read for it until someone wants to see it. The first time reads every transcript —
    /// about as long as the content index's first pass; after that only what grew.
    /// </summary>
    public async Task<UsageReport?> ReadUsageAsync(IProgress<(int Done, int Total)>? progress, CancellationToken cancellation)
    {
        await _usageGate.WaitAsync(cancellation);
        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _usageStop.Token);
            var token = stop.Token;
            var rows = await OnCatalog(() => _catalog.ListedRows());
            if (_usage is null)
            {
                var opened = await Task.Run(() => UsageLedger.OpenOrRebuild(LauncherPaths.UsageDatabase), token);
                if (_disposed)
                {
                    opened?.Dispose();          // the program closed while it opened
                    return null;
                }
                _usage = opened;
            }
            if (_usage is not { } ledger) return null;
            var codex = _codex;
            await OnBackgroundThread(() => ledger.Update(
                ledger.SourcesFor(rows, LauncherPaths.ClaudeConfigDir, codex, token), progress, token));
            var (usage, files) = await Task.Run(() => (ledger.Rows(), ledger.FileCount()), token);
            UsageUpdatedAt = DateTime.Now;
            UsageChanged?.Invoke();
            return new UsageReport(rows, usage, files);
        }
        finally
        {
            _usageGate.Release();
        }
    }

    /// <summary>Long work on a thread of its own below normal priority, so it never competes with the window.</summary>
    private static Task<T> OnBackgroundThread<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            // Gigabytes read or copied without the window ever waiting for the disk behind them.
            var background = BackgroundMode.Enter();
            try { done.SetResult(work()); }
            catch (OperationCanceledException) { done.SetCanceled(); }
            catch (Exception ex) { done.SetException(ex); }
            finally
            {
                if (background) BackgroundMode.Leave();
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "background pass",
        };
        thread.Start();
        return done.Task;
    }

    /// <summary>
    /// Waits until the user has paused. No time limit: while a menu stays open the list must
    /// not change under it, or the command picked from it would act on another row.
    /// </summary>
    private async Task WhenUserIsIdleAsync()
    {
        while (!_reportRunningSync && !_disposed && IsUserBusy?.Invoke() == true)
            await Task.Delay(400);
    }

    /// <summary>The same wait, for work no one is waiting to see.</summary>
    private async Task WhenUserPausedAsync()
    {
        while (!_disposed && IsUserBusy?.Invoke() == true) await Task.Delay(400);
    }

    private static string DescribeSync(SyncResult result)
    {
        int Records(string backend) => result.Coverage.Sources
            .Where(s => s.Backend == backend && s.Coverage == SourceCoverage.Resolved)
            .Sum(s => s.Records);
        var changes = !result.ChangedAnything ? "，没有变化"
            : (result.RowsChanged > 0 ? $"，{result.RowsChanged} 条新增或更新" : "")
              + (result.RowsRemoved > 0 ? $"，{result.RowsRemoved} 条的原记录已不在（列表里保留）" : "");
        var counts = Sources.Read
            .Where(source => source.AlwaysReported || result.Coverage.Sources.Any(s => s.Backend == source.Backend))
            .Select(source => $"{source.ColumnName} {Records(source.Backend)} 条");
        return "同步完成：" + string.Join(" · ", counts) + changes;
    }

    /// <summary>Rebuilds the group filter and then the list, in that order.</summary>
    public async Task RefreshViewAsync(bool keepDepth = false)
    {
        await RefreshGroupsAsync();
        await ReloadAsync(keepDepth: keepDepth);
    }

    /// <summary>
    /// Turns the coverage report into readable lines: source names and coverage states in
    /// Chinese, the note under each source, and the summary counts with what they mean.
    /// Everything the report contains is shown; nothing is folded into a single number.
    /// </summary>
    private void ShowCoverage(SyncResult result)
    {
        var c = result.Coverage;
        var items = new List<CoverageItemViewModel>
        {
            new($"同步于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"读到 {result.RowsWritten} 条元数据，{result.RowsChanged} 条新增或更新"
                + (result.RowsRemoved > 0 ? $"，{result.RowsRemoved} 条的原记录已不在，列表里照样保留" : "")
                + (result.Warning is null ? "" : "；" + result.Warning),
                result.Warning is null ? CoverageTone.Neutral : CoverageTone.Warn),
        };

        foreach (var s in c.Sources)
        {
            var headline = $"{BackendNames.Display(s.Backend)}：{s.Records} 条 · {CoverageText(s.Coverage)}"
                + (s.Archived > 0 ? $" · 其中应用内已归档 {s.Archived}" : "");
            items.Add(new(headline, s.Note,
                s.Coverage == SourceCoverage.Resolved ? CoverageTone.Ok : CoverageTone.Warn));
        }

        var desktop = c.Sources.FirstOrDefault(s => s.Backend == ConversationKey.DesktopCode);
        var cli = c.Sources.FirstOrDefault(s => s.Backend == ConversationKey.CliCode);
        if (desktop is { Coverage: SourceCoverage.Resolved } && cli is { Coverage: SourceCoverage.Resolved })
        {
            // Claude's two views of one history overlap; every other source counts as it stands.
            var distinct = desktop.Records + cli.Records - c.VerifiedDesktopCliLinks;
            var detail = new StringBuilder(
                $"Claude 桌面 {desktop.Records} + 终端 {cli.Records} − 两边指向同一对话的 {c.VerifiedDesktopCliLinks}");
            foreach (var source in Sources.Read.Where(s => s.Family != AppFamily.Claude))
            {
                var tally = c.Sources.FirstOrDefault(s => s.Backend == source.Backend);
                if (tally is { Coverage: SourceCoverage.Resolved })
                {
                    distinct += tally.Records;
                    detail.Append($"，加 {source.ReportName} {tally.Records}");
                }
                else if (tally is not null || source.AlwaysReported)
                    detail.Append($"，{source.ReportName} 本次没有完整读到");
            }
            items.Add(new($"本机不重复对话：{distinct} 个", detail.ToString(), CoverageTone.Ok));
        }

        items.Add(new($"桌面 ↔ 终端 已验证关联：{c.VerifiedDesktopCliLinks} 条",
            "Claude 桌面记录里的 cliSessionId 在终端 transcript 中找到了对应文件", CoverageTone.Neutral));
        items.Add(new($"未解析来源 ID：{c.UnknownSourceIds.Count} 条",
            "桌面记录指向的 transcript 不在活动根里。未解析 ≠ 丢失",
            c.UnknownSourceIds.Count == 0 ? CoverageTone.Neutral : CoverageTone.Warn));
        items.Add(new($"Claude 子代理 JSONL：{c.NonRootTranscripts} 个",
            "嵌套在项目目录下的子代理记录，不计为主会话；Codex 的子任务数见上面 Codex 一行", CoverageTone.Neutral));
        items.Add(c.GlobalUniqueConversationCount is { } total
            ? new($"全部来源唯一会话数：{total}", "所有来源都已完整读取，且关联全部解析", CoverageTone.Ok)
            : new("全部来源唯一会话数：不可得", TranslateReason(c.GlobalCountReason), CoverageTone.Warn));
        if (_archive is { } archive)
        {
            try
            {
                var size = archive.Size();
                const double Gb = 1024.0 * 1024 * 1024;
                items.Add(new($"会话管家的副本：{size.Copies} 份，记录共 {size.Bytes / Gb:0.0} GB，占用 {size.Stored / Gb:0.0} GB"
                              + (size.Plain > 0 ? $"（还有 {size.Plain} 份旧副本等空闲时压缩）" : ""),
                    "每条对话的记录文件，会话管家都在自己的数据目录里存一份（Zstandard 压缩，看和搜时直接读，不用先解压）：应用删掉或清理了原记录，"
                    + $"这里照样能看、能搜。只在你从会话管家删除对话时才删。{archive.Root}", CoverageTone.Neutral));
            }
            catch (Exception ex) when (ex is ObjectDisposedException or Microsoft.Data.Sqlite.SqliteException) { }
        }

        CoverageItems.ReplaceAll(items);

        // The one-line answer for the collapsed report. Only the three sources this tool reads
        // count here; Chat and Cowork are listed in full below as "not read", by design.
        var main = c.Sources.Where(s => Sources.Of(s.Backend) is { Read: true }).ToList();
        var needsLook = main.Count(s => s.Coverage != SourceCoverage.Resolved)
            + c.Sources.Count(s => s.Backend.StartsWith(ConversationKey.CliCode + " (其他根)", StringComparison.Ordinal))
            + (c.UnknownSourceIds.Count > 0 ? 1 : 0);
        (CoverageSummary, CoverageSummaryTone) = needsLook == 0
            ? ($"{main.Count} 个来源均已完整读取", CoverageTone.Ok)
            : ($"{needsLook} 项需要留意", CoverageTone.Warn);
    }

    private static string CoverageText(SourceCoverage coverage) => coverage switch
    {
        SourceCoverage.Resolved => "已解析",
        SourceCoverage.Partial => "部分读取",
        SourceCoverage.UnknownSource => "来源未解析",
        SourceCoverage.Unreachable => "不可达",
        _ => coverage.ToString(),
    };

    /// <summary>The rules module reports reasons in a fixed English form; show them in Chinese.</summary>
    private static string? TranslateReason(string? reason)
    {
        if (string.IsNullOrEmpty(reason)) return null;
        const string incomplete = "source-coverage-incomplete: ";
        const string unresolved = "unresolved-links: ";

        if (reason.StartsWith(incomplete, StringComparison.Ordinal))
        {
            var parts = reason[incomplete.Length..].Split(", ", StringSplitOptions.RemoveEmptyEntries)
                .Select(p =>
                {
                    var eq = p.IndexOf('=');
                    if (eq < 0) return p;
                    var state = Enum.TryParse<SourceCoverage>(p[(eq + 1)..], out var parsed)
                        ? CoverageText(parsed) : p[(eq + 1)..];
                    return $"{BackendNames.Display(p[..eq])} {state}";
                });
            return "有来源未完整读取：" + string.Join("；", parts);
        }
        if (reason.StartsWith(unresolved, StringComparison.Ordinal))
            return $"有 {reason[unresolved.Length..]} 条桌面记录的关联未解析";
        return reason;
    }

    // ------------------------------------------------------------- grouping

    /// <summary>Groups that already exist in this view, with their sizes, for the picker to offer.</summary>
    public IReadOnlyList<GroupTally> KnownGroups { get; private set; } = [];

    /// <summary>
    /// Rebuilds the filter list from the catalog, keeping the current choice when that group
    /// still exists and falling back to "all" when it does not.
    /// </summary>
    public async Task RefreshGroupsAsync()
    {
        var visibility = CurrentVisibility;
        var backend = CurrentBackend;
        var tallies = await OnCatalog(() => _catalog.Groups(visibility, backend));

        var total = tallies.Sum(t => t.Count);
        var options = new List<GroupOptionViewModel>
        {
            new(GroupFilter.All, $"全部分组（{total}）"),
        };
        if (tallies.FirstOrDefault(t => t.Name is null) is { } ungrouped)
            options.Add(new(GroupFilter.Ungrouped, $"未分组（{ungrouped.Count}）"));
        foreach (var tally in tallies.Where(t => t.Name is not null)
                                     .OrderBy(t => t.Name, StringComparer.CurrentCulture))
            options.Add(new(GroupFilter.Named(tally.Name!), $"{tally.Name}（{tally.Count}）"));

        KnownGroups = tallies.Where(t => t.Name is not null)
                             .OrderBy(t => t.Name, StringComparer.CurrentCulture)
                             .ToList();

        var wanted = _selectedGroupOption.Filter;
        _rebuildingGroups = true;
        try
        {
            GroupOptions.ReplaceAll(options);
            // Assigned directly: going through the property here would be swallowed by the
            // guard, and the caller reloads the list straight after.
            Set(ref _selectedGroupOption,
                options.FirstOrDefault(o => o.Filter == wanted) ?? options[0],
                nameof(SelectedGroupOption));
        }
        finally
        {
            _rebuildingGroups = false;
        }
    }

    /// <summary>Puts the selected conversations into one group of this launcher's own.</summary>
    public async Task AssignGroupAsync(IReadOnlyList<SessionRowViewModel> rows, string name)
    {
        var normalized = GroupRules.Normalize(name);
        if (normalized is null)
        {
            Report("分组名为空，未改动", StatusTone.Warning);
            return;
        }

        var result = await OnCatalog(() => _catalog.AssignGroup(rows.Select(r => r.Metadata), normalized));
        ReportGroupWrite(result, $"已把 {result.Applied} 条会话放入分组「{normalized}」，应用侧未改变" + Tail(result));
        await RefreshViewAsync(keepDepth: true);
    }

    /// <summary>Pins or unpins conversations: pinned ones lead the list in every order.</summary>
    public async Task SetPinnedAsync(IReadOnlyList<SessionRowViewModel> rows, bool pinned)
    {
        var result = await OnCatalog(() => _catalog.SetPinned(rows.Select(r => r.Metadata), pinned));
        ReportPreferenceWrite(result, pinned ? $"已置顶 {result.Applied} 条会话" : $"已取消置顶 {result.Applied} 条会话");
        await ReloadAsync(keepDepth: true);
    }

    /// <summary>Names a conversation in this launcher only; null goes back to the app's own title.</summary>
    public async Task RenameAsync(SessionRowViewModel row, string? title)
    {
        // Naming it what the app already calls it is no name of one's own.
        if (string.Equals(title?.Trim(), row.Metadata.Title, StringComparison.Ordinal)) title = null;
        var result = await OnCatalog(() => _catalog.SetLocalTitle(row.Metadata, title));
        ReportPreferenceWrite(result, string.IsNullOrWhiteSpace(title)
            ? "已恢复原标题"
            : $"已改名为「{title.Trim()}」——只在本工具里，{row.AppName} 里的标题不变");
        await ReloadAsync(keepDepth: true);
    }

    /// <summary>Sets or removes the user's own note on a conversation.</summary>
    public async Task SetNoteAsync(SessionRowViewModel row, string? note)
    {
        var result = await OnCatalog(() => _catalog.SetNote(row.Metadata, note));
        ReportPreferenceWrite(result, string.IsNullOrWhiteSpace(note) ? "已删除备注" : "已保存备注（搜索也能找到它）");
        await ReloadAsync(keepDepth: true);
    }

    /// <summary>Reports a pin, name or note change and, when it changed anything, offers to put it back.</summary>
    private void ReportPreferenceWrite(PreferenceWriteResult result, string text)
    {
        if (result.Applied == 0 && result.Conflict == 0)
        {
            Report("没有变化", StatusTone.Info);
            return;
        }
        Func<Task>? undo = null;
        if (result.Changes.Count > 0)
        {
            var changes = result.Changes;
            undo = async () =>
            {
                var reverted = await OnCatalog(() => _catalog.RevertPreferences(changes));
                Report("已撤销" + StaleTail(reverted.Conflict),
                    reverted.Conflict > 0 ? StatusTone.Warning : StatusTone.Success);
                await ReloadAsync(keepDepth: true);
            };
        }
        OfferUndo(undo);
        if (result.Conflict > 0) text += $"；{result.Conflict} 条已被其他操作更新，未覆盖，请同步后重试";
        Report(text, result.Conflict > 0 ? StatusTone.Warning : StatusTone.Success,
            undo is null ? null : "撤销", undo is null ? null : UndoAsync);
    }

    /// <summary>Takes the selected conversations out of whatever group they are in.</summary>
    public async Task ClearGroupAsync(IReadOnlyList<SessionRowViewModel> rows)
    {
        var result = await OnCatalog(() => _catalog.ClearGroup(rows.Select(r => r.Metadata)));
        ReportGroupWrite(result, $"已把 {result.Applied} 条会话移出分组，应用侧未改变" + Tail(result));
        await RefreshViewAsync(keepDepth: true);
    }

    /// <summary>
    /// Names a group after each conversation's working directory, for everything not grouped
    /// yet. With a few hundred transcripts this is the only practical way to start.
    /// </summary>
    public async Task GroupByProjectAsync()
    {
        var visibility = CurrentVisibility;
        var result = await OnCatalog(() => _catalog.GroupByProject(visibility));
        ReportGroupWrite(result, $"已按工作目录分组 {result.Applied} 条会话"
            + (result.Unchanged > 0 ? $"；{result.Unchanged} 条没有可用的工作目录，保持未分组" : "")
            + (result.Conflict > 0 ? $"；{result.Conflict} 条已被其他操作更新，未覆盖" : ""));
        await RefreshViewAsync(keepDepth: true);
    }

    /// <summary>How many conversations would be grouped by working directory right now.</summary>
    public Task<int> CountUngroupedAsync()
    {
        var visibility = CurrentVisibility;
        return OnCatalog(() => _catalog.Groups(visibility)
            .Where(t => t.Name is null)
            .Sum(t => t.Count));
    }

    private static string Tail(GroupWriteResult result) =>
        (result.Unchanged > 0 ? $"；{result.Unchanged} 条本来就是该分组" : "")
        + (result.Conflict > 0 ? $"；{result.Conflict} 条已被其他操作更新，未覆盖，请同步后重试" : "");

    /// <summary>Reports a group write and, when it changed anything, offers to put it back.</summary>
    private void ReportGroupWrite(GroupWriteResult result, string text)
    {
        Func<Task>? undo = null;
        if (result.Changes.Count > 0)
        {
            var changes = result.Changes;
            undo = async () =>
            {
                var reverted = await OnCatalog(() => _catalog.RevertGroups(changes));
                Report($"已撤销：{reverted.Applied} 条会话的分组已还原" + StaleTail(reverted.Conflict),
                    reverted.Conflict > 0 ? StatusTone.Warning : StatusTone.Success);
                await RefreshViewAsync(keepDepth: true);
            };
        }
        OfferUndo(undo);
        Report(text, result.Conflict > 0 ? StatusTone.Warning : StatusTone.Success,
            undo is null ? null : "撤销", undo is null ? null : UndoAsync);
    }

    // ----------------------------------------------------------------- undo

    private Func<Task>? _undo;

    /// <summary>
    /// The last organising action can be taken back — one step, like the undo link in a
    /// mail client. Every undo is guarded by the revision its action produced, so it never
    /// overwrites a change the user made after it.
    /// </summary>
    public bool CanUndo => _undo is not null;

    private void OfferUndo(Func<Task>? undo)
    {
        _undo = undo;
        OnPropertyChanged(nameof(CanUndo));
    }

    public async Task UndoAsync()
    {
        var undo = _undo;
        OfferUndo(null);
        if (undo is null)
        {
            Report("没有可以撤销的整理操作", StatusTone.Info);
            return;
        }
        await undo();
    }

    private static string StaleTail(int stale) =>
        stale > 0 ? $"；{stale} 条之后又被改动过，保持现状" : "";

    // ---------------------------------------------------------------- list

    /// <summary>
    /// Loads the list for the current view, app, search and group. A newer call supersedes
    /// an older one in flight, so a slow query can never overwrite fresher results.
    /// </summary>
    /// <param name="keepDepth">
    /// A refresh of the same view — after a sync or an organising action. It reloads as many
    /// rows as were loaded, so the pages the user scrolled into do not vanish, and leaves the
    /// list untouched when nothing in it changed.
    /// </param>
    public async Task ReloadAsync(bool debounce = false, bool keepDepth = false)
    {
        _reloadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _reloadCts = cts;

        var visibility = CurrentVisibility;
        var search = SearchText;
        var group = _selectedGroupOption.Filter;
        var backend = CurrentBackend;
        var sort = Sort;
        var since = CurrentSince;
        var mineOnly = _searchMineOnly;
        // The day edges for 今天 / 昨天 / … are fixed for this load: rows and counts agree.
        var dates = DateBuckets.At(DateTime.Now);
        var view = new ShownView(visibility, search, group, backend, sort, since, mineOnly);
        // Keeping the user's place only means something in the list they were looking at. A
        // sort clicked while a sync's refresh was starting cancels its own reload and leaves
        // this one: then it is a new list, and the old one's depth and position do not apply.
        if (keepDepth && _shown != view) keepDepth = false;
        // A list that was loaded to its end stays loaded to its end, with room for rows an
        // undo or a sync brings back; one that was not keeps the depth it had.
        var depth = !keepDepth ? SqliteCatalog.DefaultPageSize
            : Math.Max(Sessions.Count, SqliteCatalog.DefaultPageSize) + (HasMorePages ? 0 : SqliteCatalog.DefaultPageSize);

        try
        {
            if (debounce) await Task.Delay(SearchDebounceMs, cts.Token);
            var snapshot = await OnCatalog(
                () => ReadSnapshot(visibility, search, group, backend, sort, since, dates, depth, mineOnly), cts.Token);
            if (cts.IsCancellationRequested) return;

            _loadedOnce = true;
            _shown = view;
            _sections = snapshot.Sections;
            _hits = snapshot.Hits;
            _dates = dates;
            _sectionTerms = SearchKeys.Terms(search);
            // Relevance is sectioned by where the words to find are; a search that only leaves
            // words out has none, and so no headings.
            _sectionField = sort.HasSections && (sort.Field != SortField.Relevance || _sectionTerms.Count > 0)
                ? sort.Field : null;
            var terms = ContentTerms(search);
            var rows = snapshot.Rows.Select(r => NewRow(r, terms, mineOnly)).ToList();
            if (!SameRows(rows))
            {
                ShowSections = _sectionField is not null;
                ListReplacing?.Invoke(keepDepth);
                Sessions.ReplaceAll(rows);
            }
            _nextCursor = snapshot.Next;
            TotalCount = snapshot.Total;
            Views = snapshot.Views;
            OnPropertyChanged(nameof(HasMorePages));
            UpdateListSummary();
            UpdateEmptyState();
            _ = RefreshJumpListAsync();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer reload; that one reports.
        }
    }

    private int _jumpListRound;

    /// <summary>
    /// The taskbar's jump list follows the catalog: a moment after the list was reloaded —
    /// by a sync, a pin, a rename, an archive — it is worked out again, pinned first and then
    /// newest, whatever the list on screen is filtered to; Windows is only told when what it
    /// offers changed. With the setting off it holds the two apps only.
    /// </summary>
    public async Task RefreshJumpListAsync()
    {
        var round = ++_jumpListRound;
        try
        {
            await Task.Delay(1500);
            if (round != _jumpListRound || _disposed) return;
            var picked = AppSettings.Current.JumpList
                ? await OnCatalog(() => QuickOpen.Pick(RecentRows()))
                : [];
            if (_discovery is { IsCompleted: false } pending) await pending;
            var apps = _apps;
            var (claude, chatGpt) = await Task.Run(() =>
                (QuickOpen.IconOf(QuickOpenApp.Claude, apps), QuickOpen.IconOf(QuickOpenApp.ChatGpt, apps)));
            if (round != _jumpListRound || _disposed) return;
            TaskbarJumpList.Apply(picked, claude, chatGpt);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or IOException
                                       or Microsoft.Data.Sqlite.SqliteException)
        {
            // A convenience: the next reload tries again.
        }
    }

    /// <summary>Active rows in the list's usual order, a page at a time, only as far as picking reads.</summary>
    private IEnumerable<SessionMetadata> RecentRows()
    {
        PageCursor? cursor = null;
        var read = 0;
        do
        {
            var page = _catalog.Page(ViewVisibility.Active, null, cursor, 50);
            foreach (var row in page.Rows) yield return row;
            read += page.Rows.Count;
            cursor = page.Next;
        } while (cursor is not null && read < 300);
    }

    /// <summary>The words of a search that could have matched what was said; none when content is not searched.</summary>
    private IReadOnlyList<string> ContentTerms(string search) =>
        _content is null ? [] : SearchKeys.Terms(search);

    private static string? ContentTermFor(SessionMetadata row, IReadOnlyList<string> terms) =>
        terms.Count == 0 ? null : SqliteCatalog.ContentOnlyTerm(row, terms);

    /// <summary>A list row: its heading, the word only what was said in it holds, and how many messages say it.</summary>
    private SessionRowViewModel NewRow(SessionMetadata row, IReadOnlyList<string> terms, bool mineOnly)
    {
        var term = ContentTermFor(row, terms);
        var hits = term is not null && _hits.TryGetValue(term, out var counts)
                   && counts.TryGetValue(ContentIndex.KeyOf(row.Key), out var n) ? n : 0;
        return new SessionRowViewModel(row)
        {
            Section = SectionFor(row),
            ContentTerm = term,
            ContentHitCount = hits,
            ContentByUser = term is not null && mineOnly,
        };
    }

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> NoHits =
        new Dictionary<string, IReadOnlyDictionary<string, int>>();

    /// <summary>Per search word, how many messages of each conversation say it — for the rows loaded later too.</summary>
    private IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> _hits = NoHits;

    /// <summary>The filter and order a list was loaded for.</summary>
    private readonly record struct ShownView(
        ViewVisibility Visibility, string Search, GroupFilter Group, BackendFilter Backend, SortOrder Sort,
        DateTimeOffset? Since, bool MineOnly);

    /// <summary>What the rows on screen were loaded for; null before the first load.</summary>
    private ShownView? _shown;

    private sealed record Snapshot(
        IReadOnlyList<SessionMetadata> Rows, PageCursor? Next, int Total, ViewCounts Views,
        IReadOnlyDictionary<string, int> Sections, IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Hits);

    /// <summary>Rows down to <paramref name="depth"/>, and the counts for the same filter, in one catalog visit.</summary>
    private Snapshot ReadSnapshot(
        ViewVisibility visibility, string search, GroupFilter group, BackendFilter backend, SortOrder sort,
        DateTimeOffset? since, DateBuckets dates, int depth, bool mineOnly)
    {
        var rows = new List<SessionMetadata>();
        PageCursor? cursor = null;
        while (true)
        {
            var want = Math.Clamp(depth - rows.Count, 1, SqliteCatalog.MaxPageSize);
            var page = _catalog.Page(visibility, search, cursor, want, group, backend, sort, since, mineOnly);
            rows.AddRange(page.Rows);
            cursor = page.Next;
            if (cursor is null || rows.Count >= depth) break;
        }
        return new Snapshot(rows, cursor,
            _catalog.Count(visibility, search, group, backend, since, mineOnly),
            _catalog.CountViews(backend),
            _catalog.SectionCounts(visibility, search, group, backend, sort.Field, dates, since, mineOnly),
            string.IsNullOrWhiteSpace(search) ? NoHits : _catalog.ContentHitCounts(search, mineOnly));
    }

    /// <summary>The day the date sections were drawn for; after midnight 今天 has become 昨天.</summary>
    private DateBuckets _dates = DateBuckets.At(DateTime.Now);

    /// <summary>Called once a minute: redraws the list when the day has turned since it was loaded.</summary>
    public void RefreshIfDayChanged()
    {
        if (_loadedOnce && _dates.Today.Date != DateTime.Today) _ = ReloadAsync(keepDepth: true);
    }

    /// <summary>
    /// Record equality on every row, headings included: a refresh that changes nothing changes
    /// nothing on screen — no flicker, no lost scroll position.
    /// </summary>
    private bool SameRows(IReadOnlyList<SessionRowViewModel> rows)
    {
        if (rows.Count != Sessions.Count) return false;
        for (var i = 0; i < rows.Count; i++)
        {
            if (!Sessions[i].Metadata.Equals(rows[i].Metadata)) return false;
            if (!Equals(Sessions[i].Section, rows[i].Section)) return false;
            if (Sessions[i].ContentTerm != rows[i].ContentTerm) return false;
            if (Sessions[i].ContentHitCount != rows[i].ContentHitCount) return false;
            if (Sessions[i].ContentByUser != rows[i].ContentByUser) return false;
        }
        return true;
    }

    // ------------------------------------------------------------- sorting

    /// <summary>The order search results start in.</summary>
    public static readonly SortOrder RelevanceFirst = new(SortField.Relevance, false);

    private SortOrder _sort;
    private SortOrder _searchSort = RelevanceFirst;
    private SortField? _sectionField;
    private IReadOnlyList<string> _sectionTerms = [];
    private IReadOnlyDictionary<string, int> _sections = new Dictionary<string, int>();

    /// <summary>
    /// What the list is ordered by — search results keep an order of their own, most relevant
    /// first unless the user picked another. Sorting is done by the catalog, so every page follows it.
    /// </summary>
    public SortOrder Sort => IsSearching ? _searchSort : _sort;

    /// <summary>The list's order when nothing is searched for.</summary>
    public SortOrder BrowseSort => _sort;

    /// <summary>The order of search results.</summary>
    public SortOrder SearchSort => _searchSort;

    private bool _showSections;
    /// <summary>True while the list is ordered by a category and shows a heading per category.</summary>
    public bool ShowSections { get => _showSections; private set => Set(ref _showSections, value); }

    /// <summary>Starts in the orders the user left last time, without a query of its own.</summary>
    public void UseSort(SortOrder browse, SortOrder search)
    {
        _sort = browse.Field == SortField.Relevance ? SortOrder.Default : browse;
        _searchSort = search;
    }

    /// <summary>A column header was clicked: that column, or the other direction when it already was.</summary>
    public void SortBy(SortField field)
    {
        var sort = Sort;
        SetSort(field == sort.Field ? sort with { Reversed = !sort.Reversed } : new SortOrder(field, false));
    }

    /// <summary>Orders the list shown now: search results, or the list when nothing is searched for.</summary>
    public void SetSort(SortOrder sort)
    {
        if (sort == Sort) return;
        if (IsSearching) _searchSort = sort;
        else if (sort.Field == SortField.Relevance) return;       // only a search has relevance
        else _sort = sort;
        OnPropertyChanged(nameof(Sort));
        _ = ReloadAsync();
    }

    /// <summary>
    /// The heading for a row under the current order: what its category is, in words, and how
    /// many rows the whole view has in it.
    /// </summary>
    private SectionHeader? SectionFor(SessionMetadata row)
    {
        if (_sectionField is not { } field || SqliteCatalog.SectionKey(row, field, _dates, _sectionTerms) is not { } key)
            return null;
        var count = _sections.TryGetValue(key, out var n) ? n : 0;
        var countText = $"{count} 条";
        if (key == SqliteCatalog.PinnedSection)
            return new SectionHeader("置顶", countText, "你在本工具里置顶的会话：无论按什么排序都在最前");
        return field switch
        {
            SortField.LastActivity => key switch
            {
                DateBuckets.TodayKey => new SectionHeader("今天", countText),
                DateBuckets.YesterdayKey => new SectionHeader("昨天", countText),
                DateBuckets.WeekKey => new SectionHeader("7 天内", countText, "前天到 6 天前"),
                DateBuckets.MonthKey => new SectionHeader("30 天内", countText, "一周前到 30 天前"),
                DateBuckets.EarlierKey => new SectionHeader("更早", countText, "30 天以前"),
                _ => new SectionHeader("没有时间记录", countText, "来源没有记录最后活动时间"),
            },
            SortField.Group => key.Length == 0
                ? new SectionHeader("未分组", countText, "既没有本工具的分组，也没有应用自己的分组")
                : new SectionHeader(key, countText),
            SortField.Source => Sources.Of(key) is { } source
                ? new SectionHeader(source.SectionName, countText, source.SectionTooltip)
                : new SectionHeader(BackendNames.Display(key), countText),
            SortField.Project => key.Length == 0
                ? new SectionHeader("（没有记录工作目录）", countText)
                : new SectionHeader(key, countText, key),
            SortField.Relevance => key == SqliteCatalog.RelevanceNamedKey
                ? new SectionHeader("标题、名字或目录里有", countText, "每个词都出现在标题、你起的名字、备注、工作目录或会话 ID 里")
                : new SectionHeader("对话内容里提到", countText, "有的词只出现在对话内容里；提到它的消息越多越靠前"),
            SortField.Size => key switch
            {
                SqliteCatalog.SizeHugeKey => new SectionHeader("100 MB 以上", countText, "记录文件很大：多是长时间的工具输出"),
                SqliteCatalog.SizeLargeKey => new SectionHeader("10–100 MB", countText),
                SqliteCatalog.SizeMediumKey => new SectionHeader("1–10 MB", countText),
                SqliteCatalog.SizeSmallKey => new SectionHeader("1 MB 以下", countText),
                _ => new SectionHeader("没有记录文件", countText, "磁盘上找不到这条会话的记录文件"),
            },
            SortField.AppState => (AppStanding)int.Parse(key, System.Globalization.CultureInfo.InvariantCulture) switch
            {
                AppStanding.Active => new SectionHeader("应用内：活跃", countText, "在应用自己的列表里（Claude 侧边栏 / ChatGPT 任务列表）"),
                AppStanding.Archived => new SectionHeader("应用内：已归档", countText, "在应用里已归档；本工具仍然列出，正文照样能看"),
                AppStanding.EarlierPart => new SectionHeader("续接前的部分", countText,
                    "Claude 续接对话时换下的旧记录：装着较早的对话，在 Claude 侧边栏里是那条桌面会话"),
                AppStanding.TerminalOnly => new SectionHeader("仅终端", countText, "只有终端 transcript，不在 Claude 桌面侧边栏"),
                _ => new SectionHeader("未观测到应用内状态", countText),
            },
            _ => null,
        };
    }

    public async Task LoadMoreAsync()
    {
        // Scrolling near the end asks repeatedly; one page at a time is enough.
        if (_loadingMore || _nextCursor is not { } cursor) return;
        _loadingMore = true;
        try
        {
            var visibility = CurrentVisibility;
            var search = SearchText;
            var group = _selectedGroupOption.Filter;
            var backend = CurrentBackend;
            var sort = Sort;
            var since = CurrentSince;
            var mineOnly = _searchMineOnly;
            var page = await OnCatalog(
                () => _catalog.Page(visibility, search, cursor, SqliteCatalog.DefaultPageSize, group, backend, sort, since, mineOnly));

            if (page.Outcome == PageOutcome.CursorExpired)
            {
                // Never stitch a stale page onto fresh results.
                await ReloadAsync(keepDepth: true);
                return;
            }

            var terms = ContentTerms(search);
            foreach (var row in page.Rows) Sessions.Add(NewRow(row, terms, mineOnly));
            _nextCursor = page.Next;
            OnPropertyChanged(nameof(HasMorePages));
            UpdateListSummary();
        }
        finally
        {
            _loadingMore = false;
        }
    }

    private void UpdateListSummary()
    {
        var parts = new List<string>
        {
            Sessions.Count >= TotalCount ? $"共 {TotalCount} 条" : $"已显示 {Sessions.Count} / 共 {TotalCount} 条",
        };
        if (!CurrentBackend.IsAll) parts.Add("只看 " + _selectedBackendOption.Display);
        if (_selectedTimeOption.Window != TimeWindow.All) parts.Add(_selectedTimeOption.Display);
        if (_selectedGroupOption.Filter.Kind == GroupFilterKind.Named) parts.Add($"分组「{_selectedGroupOption.Filter.Name}」");
        else if (_selectedGroupOption.Filter.Kind == GroupFilterKind.Ungrouped) parts.Add("只看未分组");
        if (IsSearching)
        {
            parts.Add($"搜索「{SearchText.Trim()}」");
            if (_searchMineOnly && _content is not null) parts.Add("只搜我说的");
            if (IndexProgress is not null) parts.Add("对话内容索引还在建立，结果会陆续补全");
        }
        var sort = Sort;
        if (sort != (IsSearching ? RelevanceFirst : SortOrder.Default))
        {
            var option = SortOptionViewModel.For(sort.Field);
            parts.Add($"按{option.Display}排序（{option.Direction(sort.Reversed)}）");
        }
        if (HasMorePages) parts.Add("滚动到底部自动加载更多");
        ListSummary = string.Join("　·　", parts);
    }

    private void UpdateEmptyState()
    {
        if (Sessions.Count > 0)
        {
            Empty = null;
            return;
        }

        var search = SearchText.Trim();
        Empty = !_loadedOnce
            ? new EmptyState("正在读取会话目录…", "", null, EmptyAction.None)
            : search.Length > 0
                ? new EmptyState("没有找到匹配的会话",
                    (SearchKeys.Parse(search).Any(t => t.Exclude) ? "没有既含要找的词、又不含「-」后面那些词的会话"
                        : _content is null ? $"标题、工作目录和会话 ID 里都没有「{search}」"
                        : _searchMineOnly ? $"标题、工作目录、会话 ID 和你说过的话里都没有「{search}」"
                        : $"标题、工作目录、会话 ID 和对话内容里都没有「{search}」")
                    + (_selectedGroupOption.Filter.Kind != GroupFilterKind.All || !CurrentBackend.IsAll
                       || _selectedTimeOption.Window != TimeWindow.All
                        ? "。当前还限定了应用、时间或分组，范围更小。" : "。多个词用空格隔开时，每个词都要出现。")
                    + (IndexProgress is null ? "" : "\n对话内容索引还在建立（" + IndexProgress.Replace("正在建立对话内容索引 ", "") + "），稍后可能找到更多。"),
                    "清除搜索", EmptyAction.ClearSearch)
            : IsFiltered
                ? new EmptyState("这个筛选下没有会话",
                    ShowArchivedView ? "本工具归档里没有符合所选应用和分组的会话。" : "所选应用和分组的组合里没有会话。",
                    "显示全部", EmptyAction.ClearFilters)
            : ShowArchivedView
                ? new EmptyState("本工具归档是空的",
                    "在「活跃」列表里选中会话，按 Delete 或点「收起到本工具归档」即可收到这里。"
                    + "这只影响本工具的列表，Claude / ChatGPT 侧不变，随时可以恢复。",
                    "回到活跃列表", EmptyAction.ShowActive)
            : IsSyncing
                ? new EmptyState("正在扫描本地记录…",
                    "第一次同步要读取 Claude 与 ChatGPT 的全部会话元数据，完成后会自动列出。", null, EmptyAction.None)
                : new EmptyState("还没有读到任何会话",
                    "点「同步」重新扫描 Claude 与 ChatGPT 的本地记录；每个来源的读取结果见右下角「来源覆盖」。",
                    "立即同步", EmptyAction.Sync);
    }

    /// <summary>
    /// This launcher's own archive, applied to a whole selection at once. The apps' own
    /// history, pins and worktrees are untouched, which is why the button says "本工具".
    /// </summary>
    public async Task SetVisibilityAsync(IReadOnlyList<SessionRowViewModel> rows, bool restore)
    {
        var target = restore ? ViewVisibility.Active : ViewVisibility.ArchivedView;
        var previous = restore ? ViewVisibility.ArchivedView : ViewVisibility.Active;
        var produced = new List<(ConversationKey Key, long Revision)>();
        int already = 0, conflict = 0;

        await OnCatalog(() =>
        {
            foreach (var row in rows)
            {
                try
                {
                    var result = _catalog.SetVisibility(
                        row.Metadata.Key, target, row.Metadata.PreferenceRevision, NewOperationId());
                    switch (result.Outcome)
                    {
                        case ViewArchiveOutcome.Applied: produced.Add((row.Metadata.Key, result.Revision)); break;
                        case ViewArchiveOutcome.AlreadyApplied: already++; break;
                        default: conflict++; break;
                    }
                }
                catch (KeyNotFoundException)
                {
                    conflict++;     // removed from the catalog by a sync since it was listed
                }
            }
            return true;
        });

        Func<Task>? undo = null;
        if (produced.Count > 0)
        {
            undo = async () =>
            {
                int back = 0, stale = 0;
                await OnCatalog(() =>
                {
                    foreach (var (key, revision) in produced)
                    {
                        try
                        {
                            if (_catalog.SetVisibility(key, previous, revision, NewOperationId()).Outcome
                                == ViewArchiveOutcome.Applied) back++;
                            else stale++;
                        }
                        catch (KeyNotFoundException)
                        {
                            stale++;
                        }
                    }
                    return true;
                });
                Report($"已撤销：{back} 条回到{(restore ? "本工具归档" : "活跃列表")}" + StaleTail(stale),
                    stale > 0 ? StatusTone.Warning : StatusTone.Success);
                await RefreshViewAsync(keepDepth: true);
            };
        }
        OfferUndo(undo);

        var where = restore ? "活跃列表" : "本工具归档";
        Report((restore ? $"已把 {produced.Count} 条恢复到活跃列表" : $"已把 {produced.Count} 条收起到本工具归档")
            + "，Claude / ChatGPT 侧未改变"
            + (already > 0 ? $"；{already} 条本来就在{where}" : "")
            + (conflict > 0 ? $"；{conflict} 条已被其他操作更新，未覆盖，请同步后重试" : ""),
            conflict > 0 ? StatusTone.Warning : StatusTone.Success,
            undo is null ? null : "撤销", undo is null ? null : UndoAsync);
        await RefreshViewAsync(keepDepth: true);
    }

    private static string NewOperationId() => Guid.NewGuid().ToString("n");

    /// <summary>A status-bar link that opens a file or folder, and says so if it cannot.</summary>
    private Func<Task> OpenAction(string path) => () =>
    {
        if (Shell.Open(path) is { } error) Report("打开失败：" + error, StatusTone.Error);
        return Task.CompletedTask;
    };

    /// <summary>
    /// Builds a memory-candidate draft from a session and writes it to this tool's own
    /// drafts folder. It never touches a memory directory: the draft is for reading.
    /// </summary>
    public async Task<(string File, int Candidates, int Scanned)?> WriteMemoryDraftAsync(SessionRowViewModel row)
    {
        var path = await FindTranscriptAsync(row);
        if (path is null)
        {
            Report(ExplainMissingTranscript(row), StatusTone.Warning);
            return null;
        }

        Report("正在从你的发言里摘录记忆候选…", StatusTone.Busy);
        try
        {
            var draft = await Task.Run(() => MemoryCandidateExtractor.Extract(
                path, row.Metadata.Key.NativeSessionId, row.Metadata.Title,
                row.Metadata.ProjectRef, row.Metadata.LastActivityUtc, part: OwnPart.For(row.Metadata)));

            Directory.CreateDirectory(LauncherPaths.MemoryDraftDirectory);
            var file = Path.Combine(LauncherPaths.MemoryDraftDirectory,
                $"{row.Metadata.Key.NativeSessionId}.md");
            await File.WriteAllTextAsync(file, MemoryCandidateExtractor.Render(draft),
                new System.Text.UTF8Encoding(true));

            Report(draft.Candidates.Count == 0
                    ? $"没有找到候选（扫描了你的 {draft.UserMessagesScanned} 条发言）：{file}"
                    : $"生成 {draft.Candidates.Count} 条候选（扫描 {draft.UserMessagesScanned} 条发言）：{file}",
                StatusTone.Success, "打开草稿", OpenAction(file));
            return (file, draft.Candidates.Count, draft.UserMessagesScanned);
        }
        catch (Exception ex)
        {
            Report("生成草稿失败：" + ex.Message, StatusTone.Error);
            return null;
        }
    }

    /// <summary>
    /// Writes the whole catalog to the fixed export folder and returns that folder.
    ///
    /// <para>Exports every row, not the filtered page on screen: the point of an inventory is
    /// that nothing is missing from it, so the current search or group must not shrink it.</para>
    /// </summary>
    /// <summary>
    /// Imports Gemini's web history from Google Takeout exports into this tool's own store, then
    /// lists it. The exports are only read.
    /// </summary>
    public async Task ImportTakeoutAsync(IReadOnlyList<string> exports)
    {
        Report("正在读取 Google Takeout 的导出…", StatusTone.Busy);
        GeminiTakeoutImport result;
        try
        {
            var store = new GeminiTakeoutStore(LauncherPaths.GeminiTakeoutStore);
            result = await Task.Run(() => store.Import(exports));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or System.Text.Json.JsonException or NotSupportedException)
        {
            Report("导入失败：" + ex.Message, StatusTone.Error);
            return;
        }
        if (result.Found == 0)
        {
            Report(result.Unreadable
                    ? "这份导出里的 Gemini 记录读不出来（格式和已知的不一样）：换成 JSON 格式再导出一次（导入窗口里第 2 步）"
                    : result.ActivityFiles == 0
                        ? "导出里没有找到 Gemini 的对话：确认勾的是 My Activity（我的活动）里的 Gemini Apps，而不是单独的 Gemini"
                        : "导出里有 Gemini 的活动记录，但没有一条提问",
                StatusTone.Warning);
            return;
        }
        await SyncAsync(quiet: true);
        Report($"已导入 Gemini 网页版：这份导出有 {result.Found} 条提问，新增 {result.Added} 条；"
               + $"现在共 {result.Total} 条，{result.Conversations} 段对话。在「全部应用」里选「Gemini」只看 Gemini 的",
            StatusTone.Success);
    }

    /// <summary>
    /// Imports ChatGPT's or Claude's conversations on the web from the data export the user
    /// downloaded into this tool's own store, then lists them. The export is only read.
    /// </summary>
    public async Task ImportWebExportAsync(string backend, IReadOnlyList<string> exports)
    {
        var source = Sources.Describe(backend);
        var app = source.Family == AppFamily.Claude ? "Claude" : "ChatGPT";
        Report($"正在读取 {app} 的数据导出…", StatusTone.Busy);
        WebExportImport result;
        try
        {
            var store = WebExportStore.InDataRoot(backend);
            var progress = new ThrottledProgress<int>(n => Report($"正在读取 {app} 的数据导出… 已读 {n:N0} 段对话", StatusTone.Busy),
                _ => false);
            result = await Task.Run(() => store.Import(exports, progress));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or System.Text.Json.JsonException or NotSupportedException)
        {
            Report("导入失败：" + ex.Message, StatusTone.Error);
            return;
        }
        if (result.Found == 0)
        {
            var other = app == "Claude" ? "ChatGPT" : "Claude";
            Report(result.OtherKind
                    ? $"这是 {other} 的导出，不是 {app} 的：在「⋯」菜单里选「导入 {other} 网页版…」"
                    : result.ConversationFiles == 0
                        ? $"这份文件里没有对话列表（conversations.json）：选 {app} 邮件里下载的导出压缩包，或解压出来的 conversations.json"
                        : result.Unreadable > 0
                            ? $"导出里的 {result.Unreadable} 段对话读不出来：格式和已知的不一样"
                            : "导出里没有对话",
                StatusTone.Warning);
            return;
        }
        await SyncAsync(quiet: true);
        Report($"已导入 {source.ReportName}：这份导出有 {result.Found} 段对话，新增 {result.Added} 段、更新 {result.Updated} 段；"
               + $"现在共 {result.Total} 段"
               + (result.Unreadable > 0 ? $"（另有 {result.Unreadable} 段读不出来）" : "")
               + $"。在「全部应用」里选「{Sources.Of(source.Family).FilterName}」只看这一家的",
            StatusTone.Success);
    }

    /// <summary>Removes the imported copy of ChatGPT's or Claude's conversations; theirs on the web are untouched.</summary>
    public async Task RemoveWebExportAsync(string backend)
    {
        var source = Sources.Describe(backend);
        int removed;
        try
        {
            var store = WebExportStore.InDataRoot(backend);
            removed = await Task.Run(store.Remove);
            // Removed on purpose: not kept as "gone from the app" the way a cleaned-up record is.
            await OnCatalog(() => _catalog.Forget(backend));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report("移除失败：" + ex.Message, StatusTone.Error);
            return;
        }
        await SyncAsync(quiet: true);
        Report($"已移除导入的 {source.ReportName} 记录（{removed} 段对话）", StatusTone.Success);
    }

    /// <summary>Removes the imported copy of Gemini's web history; Google's own records are untouched.</summary>
    public async Task RemoveTakeoutAsync()
    {
        int removed;
        try
        {
            var store = new GeminiTakeoutStore(LauncherPaths.GeminiTakeoutStore);
            removed = await Task.Run(store.Remove);
            // Removed on purpose: not kept as "gone from the app" the way a cleaned-up record is.
            await OnCatalog(() => _catalog.Forget(ConversationKey.GeminiWeb));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report("移除失败：" + ex.Message, StatusTone.Error);
            return;
        }
        await SyncAsync(quiet: true);
        Report($"已移除导入的 Gemini 网页记录（{removed} 段对话）", StatusTone.Success);
    }

    public async Task<string?> ExportSessionsAsync()
    {
        Report("正在导出全部会话清单…", StatusTone.Busy);
        try
        {
            var result = await OnCatalog(() => SessionExporter.Write(
                AllRows(), LauncherPaths.ClaudeConfigDir, LauncherPaths.ExportDirectory));

            Report($"已导出 {result.Conversations} 个不重复对话 / {result.WorkingDirectories} 个工作目录：{result.Directory}",
                StatusTone.Success, "打开文件夹", OpenAction(result.Directory));
            return result.Directory;
        }
        catch (Exception ex)
        {
            Report("导出失败：" + ex.Message, StatusTone.Error);
            return null;
        }
    }

    /// <summary>Both visibilities, every page: an inventory that skipped rows would mislead.</summary>
    private List<SessionMetadata> AllRows()
    {
        var rows = new List<SessionMetadata>();
        foreach (var visibility in new[] { ViewVisibility.Active, ViewVisibility.ArchivedView })
        {
            PageCursor? cursor = null;
            do
            {
                var page = _catalog.Page(visibility, null, cursor, SqliteCatalog.MaxPageSize);
                rows.AddRange(page.Rows);
                cursor = page.Next;
            } while (cursor is not null);
        }
        return rows;
    }

    /// <summary>
    /// Path of the transcript behind a row, or null when there is none. Looked up off the UI
    /// thread: finding a Codex rollout by id walks its whole sessions tree.
    /// </summary>
    public Task<string?> FindTranscriptAsync(SessionRowViewModel row) =>
        Task.Run(() => TranscriptLocator.Find(row.Metadata, LauncherPaths.ClaudeConfigDir, _codex));

    /// <summary>
    /// Previews by transcript, valid while the file's length and write time are unchanged:
    /// going back to a row shows its preview at once, and a list refresh costs no reads.
    /// </summary>
    private readonly Dictionary<string, (long Length, DateTime WrittenUtc, ConversationPreview Preview)> _previews =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where a conversation stands — the last question and the reply to it — read from the end of its transcript.</summary>
    public async Task<ConversationPreview> PreviewAsync(SessionRowViewModel row, CancellationToken cancellation)
    {
        try
        {
            var path = await FindTranscriptAsync(row);
            cancellation.ThrowIfCancellationRequested();
            if (path is null) return ConversationPreview.Unavailable(ExplainMissingTranscript(row));

            return await Task.Run(() =>
            {
                var info = new FileInfo(path);
                var (length, written) = (info.Length, info.LastWriteTimeUtc);
                lock (_previews)
                    if (_previews.TryGetValue(path, out var known) && known.Length == length && known.WrittenUtc == written)
                        return known.Preview;

                var preview = ConversationPreview.From(
                    TranscriptReader.LastExchange(path, cancellation), row.AssistantName);
                lock (_previews)
                {
                    if (_previews.Count >= 64) _previews.Clear();     // a small cache, not a history
                    _previews[path] = (length, written, preview);
                }
                return preview;
            }, cancellation);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A preview is a convenience: whatever goes wrong reading it is said in the panel,
            // never raised as an error over the whole window.
            return ConversationPreview.Unavailable("暂时读不到这条记录：" + ex.Message);
        }
    }

    /// <summary>Shows the transcript in Explorer, selected; says so when there is none.</summary>
    public async Task RevealTranscriptAsync(SessionRowViewModel row)
    {
        var path = await FindTranscriptAsync(row);
        if (path is null)
        {
            Report(ExplainMissingTranscript(row), StatusTone.Warning);
            return;
        }
        if (Shell.Reveal(path) is { } error) Report("打开文件夹失败：" + error, StatusTone.Error);
    }

    public string ExplainMissingTranscript(SessionRowViewModel row) =>
        TranscriptLocator.ExplainMissing(row.Metadata);

    /// <summary>Runs one piece of catalog work on a worker thread, serialised with every other.</summary>
    private async Task<T> OnCatalog<T>(Func<T> work, CancellationToken cancellation = default)
    {
        await _catalogGate.WaitAsync(cancellation);
        try
        {
            // Released on the worker, not back on the UI thread: Dispose waits for this gate on
            // the UI thread, and a release queued behind that wait could never run.
            return await Task.Run(work, cancellation).ConfigureAwait(false);
        }
        finally
        {
            _catalogGate.Release();
        }
    }

    private static string LoadSchema() => CatalogSchemaText.Load();

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool _disposed;

    public void Dispose()
    {
        _disposed = true;
        _reloadCts?.Cancel();
        // The indexing thread notices at its next transcript; the index waits for its current write.
        _indexing.Cancel();
        _content?.Dispose();
        // A usage read stops at its next file; the ledger waits for the file being written.
        _usageStop.Cancel();
        _usage?.Dispose();
        TranscriptLocator.Archive = null;
        _archive?.Dispose();
        // Give an in-flight catalog task a moment to finish rather than closing the
        // connection underneath it; if it does not, the process is exiting anyway.
        if (_catalogGate.Wait(2000)) _catalog.Dispose();
    }
}
