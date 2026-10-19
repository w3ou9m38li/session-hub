using System.IO;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure;
using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Diagnostics;
using ClaudeLauncher.Infrastructure.Settings;

namespace ClaudeLauncher.App.ViewModels;

/// <summary>An export the Downloads folder holds, offered in the status bar: whose it is, and the zip.</summary>
public sealed record ExportOffer(ExportKinds Kind, FoundExport Export)
{
    /// <summary>What it is first: in a narrow status bar the end gives way.</summary>
    public string Text =>
        $"新的 {What}（下载文件夹里的 {Short(Path.GetFileName(Export.Path))}，{Size(Export.Bytes)}）";

    /// <summary>The tooltip: the text whole, what an import takes from the export, and its whole path.</summary>
    public string Details =>
        Text + (Kind == ExportKinds.GeminiTakeout ? "\n导入的是其中 Gemini 的记录" : "") + "\n" + Export.Path;

    /// <summary>A long name (ChatGPT's carry a hash) cut in the middle, so the size after it stays in sight; the whole path is the tooltip.</summary>
    private static string Short(string name) => name.Length <= 28 ? name : name[..12] + "…" + name[^15..];

    private static string Size(long bytes) => bytes >= 1L << 30
        ? $"{bytes / (1024.0 * 1024 * 1024):0.#}\u00A0GB"
        : $"{bytes / (1024.0 * 1024):0.#}\u00A0MB";

    private string What => Kind switch
    {
        ExportKinds.Claude => "Claude 数据导出",
        ExportKinds.GeminiTakeout => "Google Takeout 导出",
        _ => "ChatGPT 数据导出",
    };
}

/// <summary>
/// Imports: from the import windows, from an export offered in the status bar when one turns up
/// in Downloads, and from files dropped on the window. One at a time, on the low-priority
/// background thread, and stopped at the user's word or when the window closes.
/// </summary>
public sealed partial class MainViewModel
{
    private enum ImportEnd { Imported, Nothing, Cancelled, Failed, Busy }

    // ---------------------------------------------------------- Downloads offer

    private ExportOffer? _exportOffer;

    /// <summary>An export in the Downloads folder to offer beside the status message (never in its one slot, which may hold 撤销); null for none.</summary>
    public ExportOffer? ExportOffer { get => _exportOffer; private set => Set(ref _exportOffer, value); }

    /// <summary>Made on the first look, again only when the Downloads folder moves.</summary>
    private DownloadsScan? _downloads;

    private bool _pastFirstSync;
    private bool _lookingForExports;
    private long _exportsLookedAt;

    /// <summary>
    /// After a sync: an export downloaded since — ChatGPT's, Claude's, a Takeout with Gemini's
    /// activity — offered in the status bar. Not after the first sync of a run (start-up has disk
    /// enough to read), at most once a minute, never while an import runs, and only once the user
    /// pauses. When nothing came, the look is one look at the folder's time, on the low-priority thread.
    /// </summary>
    private async Task LookForExportsAsync()
    {
        if (_disposed || IsImporting || _lookingForExports) return;
        if (!_pastFirstSync)
        {
            _pastFirstSync = true;
            return;
        }
        if (_exportsLookedAt != 0 && Environment.TickCount64 - _exportsLookedAt < 60_000) return;
        _lookingForExports = true;
        try
        {
            await WhenUserPausedAsync();
            if (_disposed || IsImporting) return;
            _exportsLookedAt = Environment.TickCount64;
            var handled = AppSettings.Current.HandledExports;
            var scan = _downloads;
            var token = _usageStop.Token;
            var (used, offers) = await LowPriority.Run(() =>
            {
                var folder = ExportFinder.DownloadsFolder;
                if (scan is null || !string.Equals(scan.Folder, folder, StringComparison.OrdinalIgnoreCase)) scan = new DownloadsScan(folder);
                var now = DateTime.UtcNow;
                // One look at each store's time: an export imported before it is not offered.
                DateTime NotBefore(ExportKinds kind) => DownloadsScan.NotBefore(kind switch
                {
                    ExportKinds.ChatGpt => WebExportStore.InDataRoot(ConversationKey.ChatGptWeb).LastImportUtc,
                    ExportKinds.Claude => WebExportStore.InDataRoot(ConversationKey.ClaudeWeb).LastImportUtc,
                    _ => new GeminiTakeoutStore(LauncherPaths.GeminiTakeoutStore).LastImportUtc,
                }, now);
                return (scan, scan.Check(NotBefore, export => HandledExport.Contains(handled, export), token));
            }, "downloads check");
            _downloads = used;
            if (_disposed || IsImporting) return;
            // Set aside while the look ran: not brought back by it.
            var handledNow = AppSettings.Current.HandledExports;
            ExportOffer = offers.FirstOrDefault(o => !HandledExport.Contains(handledNow, o.Export)) is { } offer
                ? new ExportOffer(offer.Kind, offer.Export)
                : null;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or ArgumentException
                                       or InvalidDataException or NotSupportedException)
        {
            // An offer is a convenience: the next sync looks again.
        }
        finally
        {
            _lookingForExports = false;
        }
    }

    /// <summary>Imports the export the status bar offers.</summary>
    public async Task ImportOfferedAsync()
    {
        if (ExportOffer is not { } offer) return;
        ExportOffer = null;
        string[] exports = [offer.Export.Path];
        if (offer.Kind == ExportKinds.GeminiTakeout) await ImportTakeoutCoreAsync(exports);
        else await ImportWebCoreAsync(offer.Kind == ExportKinds.Claude ? ConversationKey.ClaudeWeb : ConversationKey.ChatGptWeb, exports);
    }

    /// <summary>The offered export set aside: not offered again. It can still be imported from the ⋯ menu.</summary>
    public void DismissOffer()
    {
        if (ExportOffer is not { } offer) return;
        ExportOffer = null;
        Remember([offer.Export], offer.Kind, HandledExport.Dismissed);
    }

    /// <summary>
    /// Files dropped on the window: each told apart off the window's thread — ChatGPT's, Claude's,
    /// a Takeout — and imported, one app after the other. What is none of them is said so.
    /// </summary>
    public async Task ImportDroppedAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        if (IsImporting)
        {
            ReportImportBusy();
            return;
        }
        Report("正在查看拖进来的文件…", StatusTone.Busy);
        var kinds = await LowPriority.Run(() => paths.Select(p => (Path: p, Kinds: ExportFinder.KindOf(p))).ToList(), "import");
        if (_disposed) return;

        // Either app's as far as it tells: read as ChatGPT's, which says so when it is Claude's.
        var chatGpt = kinds.Where(k => k.Kinds.HasFlag(ExportKinds.ChatGpt)).Select(k => k.Path).ToList();
        var claude = kinds.Where(k => k.Kinds.HasFlag(ExportKinds.Claude) && !k.Kinds.HasFlag(ExportKinds.ChatGpt)).Select(k => k.Path).ToList();
        var takeout = kinds.Where(k => k.Kinds.HasFlag(ExportKinds.GeminiTakeout)).Select(k => k.Path).ToList();
        if (chatGpt.Count + claude.Count + takeout.Count == 0)
        {
            var unknown = kinds.Select(k => Path.GetFileName(Path.TrimEndingDirectorySeparator(k.Path))).ToList();
            Report((unknown.Count == 1 ? $"认不出「{unknown[0]}」是哪家的导出" : $"认不出这 {unknown.Count} 个文件是哪家的导出")
                   + "：可以拖进 ChatGPT 或 Claude 的数据导出（压缩包，或里面的 conversations.json），"
                   + "或 Google Takeout 的压缩包（或里面 Gemini Apps 文件夹的活动记录）",
                StatusTone.Warning);
            return;
        }
        if (chatGpt.Count > 0 && (await ImportWebCoreAsync(ConversationKey.ChatGptWeb, chatGpt)) is ImportEnd.Cancelled or ImportEnd.Busy) return;
        if (claude.Count > 0 && (await ImportWebCoreAsync(ConversationKey.ClaudeWeb, claude)) is ImportEnd.Cancelled or ImportEnd.Busy) return;
        if (takeout.Count > 0) await ImportTakeoutCoreAsync(takeout);
    }

    // ---------------------------------------------------------------- imports

    /// <summary>The running import's stop; null while none is reading or writing a store.</summary>
    private CancellationTokenSource? _importStop;

    /// <summary>The store the running import writes (a backend); null while none does.</summary>
    private string? _importStore;

    /// <summary>The last import's work on its background thread; waited for, briefly, when the window closes.</summary>
    private Task? _importWork;

    /// <summary>The store being removed; no import starts meanwhile.</summary>
    private string? _removing;

    private bool _isImporting;

    /// <summary>True from the start of an import until it has said how it went, the list brought up to date included.</summary>
    public bool IsImporting { get => _isImporting; private set => Set(ref _isImporting, value); }

    /// <summary>
    /// Imports Gemini's web history from Google Takeout exports into this tool's own store, then
    /// lists it. The exports are only read.
    /// </summary>
    public Task ImportTakeoutAsync(IReadOnlyList<string> exports) => ImportTakeoutCoreAsync(exports);

    /// <summary>
    /// Imports ChatGPT's or Claude's conversations on the web from the data export the user
    /// downloaded into this tool's own store, then lists them. The export is only read.
    /// </summary>
    public Task ImportWebExportAsync(string backend, IReadOnlyList<string> exports) => ImportWebCoreAsync(backend, exports);

    private async Task<ImportEnd> ImportTakeoutCoreAsync(IReadOnlyList<string> exports)
    {
        if (StartImport(ConversationKey.GeminiWeb) is not { } stop) return ImportEnd.Busy;
        try
        {
            var cancel = CancelAction(stop);
            Report("正在读取 Google Takeout 的导出…", StatusTone.Busy, "取消", cancel);
            var progress = new ThrottledProgress<int>(n =>
            {
                if (IsReading(stop)) Report($"正在读取 Google Takeout 的导出… 已读 {n:N0} 条记录", StatusTone.Busy, "取消", cancel);
            }, _ => false);
            IReadOnlyList<FoundExport> zips = [];
            GeminiTakeoutImport? result = null;
            Exception? failure = null;
            try
            {
                var store = new GeminiTakeoutStore(LauncherPaths.GeminiTakeoutStore);
                result = await RunImportAsync(() =>
                {
                    zips = ZipsOf(exports);
                    return store.Import(exports, progress, stop.Token);
                });
            }
            catch (Exception ex) when (ex is OperationCanceledException || IsImportFailure(ex))
            {
                failure = ex;
            }
            finally
            {
                StoreWorkDone(stop);
            }
            if (_disposed) return ImportEnd.Cancelled;
            if (failure is OperationCanceledException)
            {
                // Stopped while reading: nothing was written.
                Report("已取消导入 Google Takeout 的导出：已导入的记录没有改动", StatusTone.Warning);
                return ImportEnd.Cancelled;
            }
            if (failure is not null || result is null)
            {
                if (IsExportsFault(failure)) Remember(zips, ExportKinds.GeminiTakeout, HandledExport.NotImportable);
                Report("导入失败：" + failure?.Message, StatusTone.Error);
                return ImportEnd.Failed;
            }
            if (result.Found == 0)
            {
                Remember(zips, ExportKinds.GeminiTakeout, HandledExport.NotImportable);
                Report(result.Unreadable
                        ? "这份导出里的 Gemini 记录读不出来（格式和已知的不一样）：换成 JSON 格式再导出一次（导入窗口里第 2 步）"
                        : result.ActivityFiles == 0
                            ? "导出里没有找到 Gemini 的对话：确认勾的是 My Activity（我的活动）里的 Gemini Apps，而不是单独的 Gemini"
                            : "导出里有 Gemini 的活动记录，但没有一条提问",
                    StatusTone.Warning);
                return ImportEnd.Nothing;
            }
            // Before the sync: the look for exports after it must not offer this one again.
            Remember(zips, ExportKinds.GeminiTakeout, HandledExport.Imported);
            Report("已读完 Google Takeout 的导出，正在列出导入的对话…", StatusTone.Busy);
            await SyncAfterStoreChangeAsync();
            if (_disposed) return ImportEnd.Imported;
            Report((stop.IsCancellationRequested ? "取消时已经在写入，导入照常完成。" : "")
                   + $"已导入 Gemini 网页版：这份导出有 {result.Found} 条提问，新增 {result.Added} 条；"
                   + $"现在共 {result.Total} 条，{result.Conversations} 段对话。在「全部应用」里选「Gemini」只看 Gemini 的",
                StatusTone.Success);
            return ImportEnd.Imported;
        }
        finally
        {
            IsImporting = false;
        }
    }

    private async Task<ImportEnd> ImportWebCoreAsync(string backend, IReadOnlyList<string> exports)
    {
        var source = Sources.Describe(backend);
        var app = source.Family == AppFamily.Claude ? "Claude" : "ChatGPT";
        var kind = backend == ConversationKey.ClaudeWeb ? ExportKinds.Claude : ExportKinds.ChatGpt;
        if (StartImport(backend) is not { } stop) return ImportEnd.Busy;
        try
        {
            var cancel = CancelAction(stop);
            Report($"正在读取 {app} 的数据导出…", StatusTone.Busy, "取消", cancel);
            var progress = new Counted(new ThrottledProgress<int>(n =>
            {
                if (IsReading(stop)) Report($"正在读取 {app} 的数据导出… 已读 {n:N0} 段对话", StatusTone.Busy, "取消", cancel);
            }, _ => false));
            IReadOnlyList<FoundExport> zips = [];
            WebExportImport? result = null;
            Exception? failure = null;
            try
            {
                var store = WebExportStore.InDataRoot(backend);
                result = await RunImportAsync(() =>
                {
                    zips = ZipsOf(exports);
                    return store.Import(exports, progress, stop.Token);
                });
            }
            catch (Exception ex) when (ex is OperationCanceledException || IsImportFailure(ex))
            {
                failure = ex;
            }
            finally
            {
                StoreWorkDone(stop);
            }
            if (_disposed) return ImportEnd.Cancelled;
            if (failure is OperationCanceledException)
            {
                // Each conversation is written whole before the next is read: those written stay, and are listed.
                // Written since it arrived, the export is no longer offered or suggested: where to find it again is said.
                var kept = progress.Last;
                Report(kept > 0
                        ? $"已取消导入 {app} 的数据导出：已读完的 {kept:N0} 段对话已保存，其余的没有导入；"
                          + $"要补上，把这份导出再拖进窗口，或在「⋯」菜单里选「导入 {app} 网页版…」再选它"
                        : $"已取消导入 {app} 的数据导出：什么都没有改动",
                    StatusTone.Warning);
                if (kept > 0) await SyncAfterStoreChangeAsync();
                return ImportEnd.Cancelled;
            }
            if (failure is not null || result is null)
            {
                if (IsExportsFault(failure)) Remember(zips, kind, HandledExport.NotImportable);
                Report("导入失败：" + failure?.Message, StatusTone.Error);
                return ImportEnd.Failed;
            }
            if (result.Found == 0)
            {
                var other = app == "Claude" ? "ChatGPT" : "Claude";
                // The other app's export may still be offered as that app's.
                if (!result.OtherKind) Remember(zips, kind, HandledExport.NotImportable);
                Report(result.OtherKind
                        ? $"这是 {other} 的导出，不是 {app} 的：在「⋯」菜单里选「导入 {other} 网页版…」"
                        : result.ConversationFiles == 0
                            ? $"这份文件里没有对话列表（conversations.json）：选 {app} 邮件里下载的导出压缩包，或解压出来的 conversations.json"
                            : result.Unreadable > 0
                                ? $"导出里的 {result.Unreadable} 段对话读不出来：格式和已知的不一样"
                                : "导出里没有对话",
                    StatusTone.Warning);
                return ImportEnd.Nothing;
            }
            // Before the sync: the look for exports after it must not offer this one again.
            Remember(zips, kind, HandledExport.Imported);
            Report($"已读完 {app} 的数据导出，正在列出导入的对话…", StatusTone.Busy);
            await SyncAfterStoreChangeAsync();
            if (_disposed) return ImportEnd.Imported;
            Report($"已导入 {source.ReportName}：这份导出有 {result.Found} 段对话，新增 {result.Added} 段、更新 {result.Updated} 段"
                   + (result.Older > 0 ? $"，{result.Older} 段比已导入的旧，保留了已有的" : "")
                   + $"；现在共 {result.Total} 段"
                   + (result.Unreadable > 0 ? $"（另有 {result.Unreadable} 段读不出来）" : "")
                   + $"。在「全部应用」里选「{Sources.Of(source.Family).FilterName}」只看这一家的",
                StatusTone.Success);
            return ImportEnd.Imported;
        }
        finally
        {
            IsImporting = false;
        }
    }

    /// <summary>Removes the imported copy of ChatGPT's or Claude's conversations; theirs on the web are untouched.</summary>
    public async Task RemoveWebExportAsync(string backend)
    {
        var source = Sources.Describe(backend);
        int removed;
        // Taken first: no import starts while a running one is being stopped either.
        _removing = backend;
        try
        {
            await StopImportIntoAsync(backend);
            var store = WebExportStore.InDataRoot(backend);
            removed = await Task.Run(store.Remove);
            // A sync that read the store before it went writes what it read first; forgotten after it.
            await SyncsFinishedAsync();
            // Removed on purpose: not kept as "gone from the app" the way a cleaned-up record is.
            await OnCatalog(() => _catalog.Forget(backend));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report("移除失败：" + ex.Message, StatusTone.Error);
            return;
        }
        finally
        {
            _removing = null;
        }
        await SyncAfterStoreChangeAsync();
        Report($"已移除导入的 {source.ReportName} 记录（{removed} 段对话）", StatusTone.Success);
    }

    /// <summary>Removes the imported copy of Gemini's web history; Google's own records are untouched.</summary>
    public async Task RemoveTakeoutAsync()
    {
        int removed;
        // Taken first: no import starts while a running one is being stopped either.
        _removing = ConversationKey.GeminiWeb;
        try
        {
            await StopImportIntoAsync(ConversationKey.GeminiWeb);
            var store = new GeminiTakeoutStore(LauncherPaths.GeminiTakeoutStore);
            removed = await Task.Run(store.Remove);
            // A sync that read the store before it went writes what it read first; forgotten after it.
            await SyncsFinishedAsync();
            // Removed on purpose: not kept as "gone from the app" the way a cleaned-up record is.
            await OnCatalog(() => _catalog.Forget(ConversationKey.GeminiWeb));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report("移除失败：" + ex.Message, StatusTone.Error);
            return;
        }
        finally
        {
            _removing = null;
        }
        await SyncAfterStoreChangeAsync();
        Report($"已移除导入的 Gemini 网页记录（{removed} 段对话）", StatusTone.Success);
    }

    /// <summary>Takes the one import slot for <paramref name="store"/>; null, with the reason said, when it is taken.</summary>
    private CancellationTokenSource? StartImport(string store)
    {
        if (IsImporting)
        {
            ReportImportBusy();
            return null;
        }
        if (_removing is not null)
        {
            Report("正在移除导入的记录：移除完成后再导入", StatusTone.Warning);
            return null;
        }
        var stop = new CancellationTokenSource();
        _importStop = stop;
        _importStore = store;
        IsImporting = true;
        ExportOffer = null;     // offered again at the next look, if it is still to do
        return stop;
    }

    private void ReportImportBusy()
    {
        var running = _importStop;
        Report("另一份导出正在导入：等它完成再导入这一份", StatusTone.Warning,
            "取消正在导入的", running is null ? null : CancelAction(running));
    }

    /// <summary>The status bar's 取消: the import stops at its next conversation or record.</summary>
    private Func<Task> CancelAction(CancellationTokenSource stop) => () =>
    {
        if (IsReading(stop))
        {
            stop.Cancel();
            Report("正在取消导入…", StatusTone.Busy);
        }
        return Task.CompletedTask;
    };

    /// <summary>Whether the import of <paramref name="stop"/> is still at its store and not asked to stop.</summary>
    private bool IsReading(CancellationTokenSource stop) => _importStop == stop && !stop.IsCancellationRequested;

    private async Task<T> RunImportAsync<T>(Func<T> work)
    {
        var running = LowPriority.Run(work, "import");
        _importWork = running;
        return await running;
    }

    /// <summary>The import is done with its store: nothing left to cancel.</summary>
    private void StoreWorkDone(CancellationTokenSource stop)
    {
        if (_importStop == stop)
        {
            _importStop = null;
            _importStore = null;
        }
        stop.Dispose();
    }

    /// <summary>A running import into <paramref name="store"/> stopped and waited for: removing the store under it would bring rows back.</summary>
    private async Task StopImportIntoAsync(string store)
    {
        if (_importStore != store || _importStop is not { } stop) return;
        stop.Cancel();
        if (_importWork is { } work) await Task.WhenAny(work);
    }

    /// <summary>
    /// On closing: a running import stops at its next conversation or record (a Takeout already
    /// writing finishes), and is given a moment for that so the process does not end part way through a write.
    /// </summary>
    private void StopImport()
    {
        _importStop?.Cancel();
        if (_importWork is { } work) Task.WaitAny([work], TimeSpan.FromSeconds(3));
    }

    private static bool IsImportFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException
            or NotSupportedException or ArgumentException;

    /// <summary>A failure of the export itself — not a zip, a damaged one — rather than of reading it just now.</summary>
    private static bool IsExportsFault(Exception? ex) =>
        ex is InvalidDataException or System.Text.Json.JsonException or NotSupportedException;

    /// <summary>The zips among the exports, as the Downloads offer knows them: looked at before the import reads them.</summary>
    private static List<FoundExport> ZipsOf(IReadOnlyList<string> exports)
    {
        var zips = new List<FoundExport>();
        foreach (var export in exports)
        {
            if (!export.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var file = new FileInfo(export);
                if (file.Exists) zips.Add(new FoundExport(file.FullName, file.LastWriteTimeUtc, file.Length));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // Not remembered: at worst offered once more.
            }
        }
        return zips;
    }

    /// <summary>Remembers how exports were handled, so the status bar does not offer them again.</summary>
    private static void Remember(IReadOnlyList<FoundExport> zips, ExportKinds kind, string outcome)
    {
        if (zips.Count == 0) return;
        AppSettings.Update(s => s with
        {
            HandledExports = zips.Aggregate(s.HandledExports,
                (list, zip) => HandledExport.Add(list, new HandledExport(zip.Path, zip.Bytes, zip.SavedUtc, kind.ToString(), outcome))),
        });
    }

    /// <summary>Passes progress on, and keeps the last count where the window can read it once the work has stopped.</summary>
    private sealed class Counted(IProgress<int> next) : IProgress<int>
    {
        private int _last;

        public int Last => Volatile.Read(ref _last);

        public void Report(int value)
        {
            Volatile.Write(ref _last, value);
            next.Report(value);
        }
    }
}
