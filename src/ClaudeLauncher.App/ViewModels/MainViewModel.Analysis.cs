using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure;
using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Search;
using ClaudeLauncher.Infrastructure.Usage;

namespace ClaudeLauncher.App.ViewModels;

/// <summary>
/// What the usage ledger keeps about how conversations used their context — the largest
/// context, compactions, cache rewrites after a pause — for the side panel and the usage
/// window. Read from the ledger the usage window started; nothing is read or kept otherwise.
/// The analysis window itself reads transcripts on its own and needs none of this.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>One conversation's context summary as the ledger last kept it; null without a ledger or any count.</summary>
    public async Task<SessionMetrics?> MetricsOfAsync(SessionRowViewModel row)
    {
        if (_usage is not { } ledger) return null;
        try
        {
            return await Task.Run(() => ledger.Metrics(ContentIndex.KeyOf(row.Metadata.Key)));
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException
                                       or Microsoft.Data.Sqlite.SqliteException)
        {
            return null;
        }
    }

    /// <summary>Every conversation's context summary, and the cache rewrites by conversation and day.</summary>
    public sealed record ContextReport(IReadOnlyDictionary<string, SessionMetrics> Metrics, IReadOnlyList<RewriteRow> Rewrites);

    /// <summary>
    /// For the usage window, after <see cref="ReadUsageAsync"/> brought the ledger up to date:
    /// what it ranks and describes conversations by besides their tokens. Null without a ledger.
    /// </summary>
    public async Task<ContextReport?> ReadContextAsync(CancellationToken cancellation)
    {
        if (_usage is not { } ledger) return null;
        return await Task.Run(() => new ContextReport(ledger.AllMetrics(), ledger.RewriteRows()), cancellation);
    }

    /// <summary>The transcript of a conversation the usage window names, or null when there is none.</summary>
    public Task<string?> FindTranscriptAsync(SessionMetadata row) =>
        Task.Run(() => TranscriptLocator.Find(row, LauncherPaths.ClaudeConfigDir, _codex));
}
