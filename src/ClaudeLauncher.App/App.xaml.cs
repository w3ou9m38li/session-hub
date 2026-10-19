using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure;
using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Catalog;
using ClaudeLauncher.Infrastructure.Discovery;
using ClaudeLauncher.Infrastructure.Launch;

namespace ClaudeLauncher.App;

public partial class App : Application
{
    private const string DefaultInstanceName = @"Local\ClaudeSessionLauncher.SingleInstance";

    /// <summary>
    /// Per-user single instance. The mutex is scoped to this launcher's own window only; it is
    /// never treated as a lock on Claude, which has its own lifecycle and may be started or
    /// closed by the user at any time. A data root set explicitly with CLAUDE_LAUNCHER_DATA_DIR
    /// gets its own instance, since a separate catalog cannot collide with this one — named from
    /// the variable, so every launch with it meets the same name. When the variable did not take
    /// (refused, or its drive not ready), the launch is on the default root and gets its name.
    /// </summary>
    private static string InstanceName { get; } = NameInstance();

    private static string NameInstance()
    {
        var explicitRoot = Environment.GetEnvironmentVariable(LauncherPaths.DataRootVariable);
        // A variable that did not take leaves this copy on the default root, which has one window:
        // the everyday one, the only one that moves the data folder.
        if (string.IsNullOrWhiteSpace(explicitRoot) || !LauncherPaths.DataRootFromVariable) return DefaultInstanceName;
        string root;
        try { root = Path.GetFullPath(explicitRoot.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            root = explicitRoot.Trim();
        }
        var key = Encoding.UTF8.GetBytes(root.TrimEnd('\\', '/').ToUpperInvariant());
        return DefaultInstanceName + "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(key))[..12];
    }

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activation;
    private RegisteredWaitHandle? _activationWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        // An entry of the taskbar's jump list: open one conversation in its own app, or start
        // one of the apps, and leave. No window and no single-instance handover — a running
        // copy of this tool has nothing to do with it, and none needs to be running.
        if (e.Args.Length >= 1 && e.Args[0] is QuickOpen.OpenArgument or QuickOpen.LaunchArgument)
        {
            RunQuickOpen(e.Args);
            Shutdown();
            return;
        }

        // Headless modes: export the inventory or generate memory-candidate drafts without
        // opening a window, so both can be run from a shortcut or a scheduled task.
        if (e.Args.Length >= 1 && e.Args[0] is "--draft" or "--export")
        {
            // A headless run has no window to show an error in, so a failure must land in a
            // file. Dying silently with only an exit code is not a usable failure mode.
            try
            {
                if (e.Args[0] == "--export") RunExportMode();
                else RunDraftMode(e.Args.Length >= 2 ? e.Args[1] : "all");
            }
            catch (Exception ex)
            {
                try
                {
                    var crashDir = e.Args[0] == "--draft"
                        ? LauncherPaths.MemoryDraftDirectory
                        : LauncherPaths.ExportDirectory;
                    Directory.CreateDirectory(crashDir);
                    File.WriteAllText(
                        Path.Combine(crashDir, "_crash.txt"),
                        DateTimeOffset.Now.ToString("O") + "\n" + ex, new UTF8Encoding(true));
                }
                catch { /* nothing left to do */ }
            }
            Shutdown();
            return;
        }

        _instanceMutex = new Mutex(initiallyOwned: true, InstanceName, out var isFirstInstance);

        // Started by Restart(): the previous copy is closing and hands the name over as it exits.
        if (!isFirstInstance && e.Args.Contains(RestartedArgument) && TakeOver(_instanceMutex))
            isFirstInstance = true;

        if (!isFirstInstance)
        {
            // A second launch brings the existing window forward instead of opening a
            // duplicate catalog connection.
            ActivateRunningInstance();
            Shutdown();
            return;
        }

        // A second launch signals this; the list comes forward even from behind a reader.
        _activation = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Activate");
        _activationWait = ThreadPool.RegisterWaitForSingleObject(_activation,
            (_, _) => Dispatcher.BeginInvoke(BringMainWindowForward), null, Timeout.Infinite, executeOnlyOnce: false);

        // A crash in an event handler should say what happened, not vanish silently.
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            // Owned by the window in use. The list may be minimised while a transcript is
            // read, and a dialog owned by a minimised window is hidden along with it — every
            // window would then seem frozen behind a dialog nobody can see.
            var owner = Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                        ?? (MainWindow is { WindowState: not WindowState.Minimized } main ? main : null);
            MessageDialog.Show(owner, DialogKind.Error, "出现了一个意外错误",
                args.Exception.Message + "\n\n这次操作没有完成；Claude 与 ChatGPT 的数据只读、不受影响。"
                + "如果反复出现，请把这段文字发给维护者。");
        };

        // Before the settings are read: a move from the old folder brings them along, and a move
        // that stops short keeps this run there (MainViewModel gets the same report back). Only the
        // everyday window moves: a copy started with CLAUDE_LAUNCHER_DATA_DIR has a name of its own
        // even when the variable did not take, and must not move the folder that window runs on.
        DataRootMigrator.PrepareDataRoot(mayMove: InstanceName == DefaultInstanceName);

        // Light or dark, before any window looks a colour up.
        Theme.Apply(Resources.MergedDictionaries[0], AppSettings.Current.Theme);
        ViewModels.Palette.Load(Resources.MergedDictionaries[0]);

        base.OnStartup(e);

        // Built here rather than through StartupUri: WPF builds its start-up window even after
        // an early exit above has called Shutdown — the whole catalog opened, unseen, for a
        // start that only hands a link on or brings the running copy forward.
        try
        {
            MainWindow = new MainWindow();
        }
        catch (CatalogUnavailableException ex)
        {
            // Nothing to list without the catalog: say why and leave, rather than stay running
            // with no window and hold the single-instance name. The next start tries again.
            MessageDialog.Show(null, DialogKind.Error, "会话目录打不开", ex.Message);
            Shutdown();
            return;
        }
        MainWindow.Show();
    }

    /// <summary>
    /// Starts a fresh copy and closes this one — the way a change of theme, which is chosen at
    /// start-up, takes effect. The single-instance name is given up first, or the new copy
    /// would only bring this window forward and leave.
    /// </summary>
    public void Restart()
    {
        // The new copy reads the settings as it starts: the one just changed must be on disk.
        AppSettings.Flush();
        try
        {
            // This copy keeps the single-instance name until it exits; the new one waits for it.
            // Were the start to fail, this copy stays the only one, as before.
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            start.ArgumentList.Add(RestartedArgument);
            using var _ = Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageDialog.Show(MainWindow, DialogKind.Warning, "没能重新打开", ex.Message + "\n\n请手动打开会话管家。");
            return;
        }
        (MainWindow as MainWindow)?.ExitForRestart();
    }

    /// <summary>Restores the list window (maximised stays maximised) and brings it to the front.</summary>
    private void BringMainWindowForward()
    {
        // Minimised, behind other windows or put away in the tray: the same way back.
        if (MainWindow is MainWindow { IsLoaded: true } window) window.ShowFromBackground();
    }

    private const int SwRestore = 9;
    private const uint AsfwAny = unchecked((uint)-1);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    /// <summary>
    /// Asks the first instance to show its list window. Only that process knows which of its
    /// windows is the list — transcript windows are top-level windows too, and the one
    /// Windows calls the "main" window is simply whichever is highest. This process was just
    /// started by the user, which is what lets it pass the foreground on at all.
    /// </summary>
    private static void ActivateRunningInstance()
    {
        AllowSetForegroundWindow(AsfwAny);
        try
        {
            using var signal = EventWaitHandle.OpenExisting(InstanceName + ".Activate");
            signal.Set();
            return;
        }
        catch (WaitHandleCannotBeOpenedException) { /* a build from before the signal existed */ }
        catch (UnauthorizedAccessException) { }

        using var self = Process.GetCurrentProcess();
        foreach (var other in Process.GetProcessesByName(self.ProcessName))
        {
            using (other)
            {
                if (other.Id == self.Id) continue;
                var hwnd = other.MainWindowHandle;
                if (hwnd == IntPtr.Zero) continue;
                if (IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
                SetForegroundWindow(hwnd);
                return;
            }
        }
    }

    /// <summary>
    /// Does what a jump-list entry names. Nothing is said when it works — the app coming up is
    /// the answer; a failure is shown in the tool's own dialog, since a click that silently does
    /// nothing is the one outcome worse than an error.
    /// </summary>
    private void RunQuickOpen(string[] args)
    {
        string? failure;
        try
        {
            var apps = WindowsAppDiscovery.Discover();
            if (QuickOpen.ParseOpen(args) is { } target)
            {
                // Off this thread: waiting for ChatGPT to start must not hold the dispatcher it runs on.
                var opened = Task.Run(() => QuickOpen.OpenAsync(target.Backend, target.Id, apps)).GetAwaiter().GetResult();
                failure = opened.Outcome == DeepLinkOutcome.Requested ? null : opened.Message;
            }
            else if (QuickOpen.ParseLaunch(args) is { } app)
            {
                QuickOpen.LetTheAppComeForward();
                var launched = app == QuickOpenApp.Claude
                    ? WindowsClaudeLauncher.Activate(apps.Claude)
                    : ChatGptLauncher.Open(apps.ChatGpt);
                failure = launched.Outcome is LaunchOutcome.ActivationRequested or LaunchOutcome.AlreadyRunning
                    ? null : launched.Message;
            }
            else
            {
                failure = "任务栏跳转列表传来的参数认不出来：" + string.Join(' ', args)
                          + "\n\n可能是旧版本留下的条目；打开一次会话管家，列表就会更新。";
            }
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }
        if (failure is null) return;

        Theme.Apply(Resources.MergedDictionaries[0], AppSettings.Current.Theme);
        ViewModels.Palette.Load(Resources.MergedDictionaries[0]);
        MessageDialog.Show(null, DialogKind.Error, "没能打开", failure);
    }

    /// <summary>
    /// Writes drafts for one session id (prefix match) or for every catalog row ("all").
    /// Output goes only to this tool's drafts folder — never into a memory directory.
    /// </summary>
    private static void RunDraftMode(string target)
    {
        // Never moves the data folder: the window may be running on the old one.
        DataRootMigrator.PrepareDataRoot(mayMove: false);

        var schema = CatalogSchemaText.Load();
        using var catalog = SqliteCatalog.OpenOrRebuild(
            LauncherPaths.CatalogDatabase, schema, out var rebuiltFrom);

        var rows = ReadAllRows(catalog);

        var selected = target == "all"
            ? rows
            : rows.Where(r =>
                    r.Key.NativeSessionId.StartsWith(target, StringComparison.OrdinalIgnoreCase)
                    || (r.CliSessionId ?? string.Empty).StartsWith(target, StringComparison.OrdinalIgnoreCase))
                  .ToList();

        var log = new StringBuilder();
        if (rebuiltFrom is not null)
            log.AppendLine($"目录数据库损坏，已重建（原文件移至 {rebuiltFrom}，本工具的归档/置顶偏好已丢失）");
        log.AppendLine($"目录共 {rows.Count} 条，选中 {selected.Count} 条");
        log.AppendLine();

        int written = 0, noTranscript = 0, empty = 0;
        // This tool's copies are read where an app no longer has a transcript, or keeps it only
        // compressed — and only read: the window may be running and writing them meanwhile.
        TranscriptArchive? archive = null;
        try
        {
            archive = new TranscriptArchive(LauncherPaths.ArchiveDirectory, readOnly: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            // No copies yet: the apps' own transcripts only.
        }
        TranscriptLocator.Archive = archive;
        try
        {
            // Every folder listed once, not once per row.
            var found = TranscriptLocator.FindAll(selected, LauncherPaths.ClaudeConfigDir, CodexPaths.Discover());
            noTranscript = selected.Count - found.Count;
            foreach (var (row, path) in found)
            {
                try
                {
                    var draft = MemoryCandidateExtractor.Extract(
                        path, row.Key.NativeSessionId, row.Title, row.ProjectRef, row.LastActivityUtc, part: OwnPart.For(row));

                    if (draft.Candidates.Count == 0)
                    {
                        empty++;
                        continue;
                    }

                    var file = Path.Combine(
                        LauncherPaths.MemoryDraftDirectory, row.Key.NativeSessionId + ".md");
                    File.WriteAllText(file, MemoryCandidateExtractor.Render(draft), new UTF8Encoding(true));
                    written++;

                    var title = row.Title.Length > 40 ? row.Title[..40] : row.Title;
                    log.AppendLine($"{draft.Candidates.Count,3} 条候选  {title}");
                }
                catch (Exception ex)
                {
                    log.AppendLine($"失败  {row.Key.NativeSessionId}  {ex.Message}");
                }
            }
        }
        finally
        {
            TranscriptLocator.Archive = null;
            archive?.Dispose();
        }

        log.AppendLine();
        log.AppendLine($"生成 {written} 份草稿；{empty} 条无候选；{noTranscript} 条找不到 transcript");
        log.AppendLine($"输出目录：{LauncherPaths.MemoryDraftDirectory}");

        File.WriteAllText(
            Path.Combine(LauncherPaths.MemoryDraftDirectory, "_run-log.txt"),
            log.ToString(), new UTF8Encoding(true));
    }

    /// <summary>
    /// Every row across both visibilities. An export or a bulk draft that silently omitted
    /// archived rows would under-report, which is the one thing an inventory must not do.
    /// </summary>
    private static List<SessionMetadata> ReadAllRows(SqliteCatalog catalog)
    {
        var rows = new List<SessionMetadata>();
        // Qualified: WPF has its own Visibility, and the ambiguity is a compile error here.
        foreach (var visibility in new[]
                 { Core.Contracts.Visibility.Active, Core.Contracts.Visibility.ArchivedView })
        {
            PageCursor? cursor = null;
            do
            {
                var page = catalog.Page(visibility, null, cursor, SqliteCatalog.MaxPageSize);
                rows.AddRange(page.Rows);
                cursor = page.Next;
            } while (cursor is not null);
        }
        return rows;
    }

    /// <summary>
    /// Writes the session inventory to its fixed folder without opening a window, so it can be
    /// refreshed from a shortcut or a scheduled run.
    /// </summary>
    private static void RunExportMode()
    {
        // Never moves the data folder: the window may be running on the old one.
        DataRootMigrator.PrepareDataRoot(mayMove: false);

        var schema = CatalogSchemaText.Load();
        using var catalog = SqliteCatalog.OpenOrRebuild(
            LauncherPaths.CatalogDatabase, schema, out var rebuiltFrom);

        // Export what the apps' files say right now, not what a stale catalog remembers.
        var apps = WindowsAppDiscovery.Discover();
        new CatalogSyncService(catalog, LauncherPaths.ClaudeConfigDir, GeminiPaths.Discover(),
            new GeminiTakeoutStore(LauncherPaths.GeminiTakeoutStore), LauncherPaths.ProbeCacheFile,
            () => DshPaths.Discover(AppSettings.Current.DshHomes), WebExportStore.InDataRoot())
            .Sync(apps.Claude, CodexPaths.Discover());

        var rows = ReadAllRows(catalog);
        var result = SessionExporter.Write(
            rows, LauncherPaths.ClaudeConfigDir, LauncherPaths.ExportDirectory);

        var log = new StringBuilder();
        if (rebuiltFrom is not null)
            log.AppendLine($"目录数据库损坏，已重建（原文件移至 {rebuiltFrom}）");
        log.AppendLine($"不重复对话 {result.Conversations} 个，其中 {result.AlreadyInSidebar} 个已在侧边栏");
        log.AppendLine($"工作目录 {result.WorkingDirectories} 个，已信任 {result.TrustedDirectories} 个");
        log.AppendLine($"输出目录：{result.Directory}");

        File.WriteAllText(
            Path.Combine(LauncherPaths.ExportDirectory, "_run-log.txt"),
            log.ToString(), new UTF8Encoding(true));
    }

    private const string RestartedArgument = "--restarted";

    /// <summary>Waits for the previous copy to let go of the single-instance name; false if it never does.</summary>
    private static bool TakeOver(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(TimeSpan.FromSeconds(20));
        }
        catch (AbandonedMutexException)
        {
            return true;        // it exited without releasing: the name is ours now
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppSettings.Flush();
        _activationWait?.Unregister(null);
        _activation?.Dispose();
        // Released rather than left to the process's end, so a restarting copy takes over at once.
        try { _instanceMutex?.ReleaseMutex(); }
        catch (ApplicationException) { /* never held: a second launch that only activated the first */ }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
