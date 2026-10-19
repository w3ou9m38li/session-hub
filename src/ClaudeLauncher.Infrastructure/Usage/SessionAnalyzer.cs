using System.Globalization;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Changes;

namespace ClaudeLauncher.Infrastructure.Usage;

/// <summary>A transcript of the conversation analysed — its own, a subagent's or a spawned task's — and what to call it.</summary>
public sealed record AnalysisFile(string Path, AppFamily App, string Label);

/// <summary>One call: what it read and wrote, and what happened to the context since the call before it.</summary>
/// <param name="Gap">Time since the call before it on the same line; null for the first.</param>
/// <param name="LongGap">Longer than the cache keeps what was written.</param>
/// <param name="MarkedCompaction">The app marked the compaction; otherwise it is seen in the context halving.</param>
/// <param name="Rewritten">Tokens written (or read at full price) again because the cache had expired; 0 when it had not.</param>
public sealed record AnalysisCall(
    DateTimeOffset At, string Model, long Input, long CacheWrite, long CacheRead, long Output,
    TimeSpan? Gap, bool LongGap, bool Compaction, bool MarkedCompaction, long Rewritten)
{
    /// <summary>What the call read in: fresh input, cache writes and cache reads.</summary>
    public long Context => Input + CacheWrite + CacheRead;
}

/// <summary>One line of calls with a cache of its own: the conversation itself, a subagent, a spawned task.</summary>
public sealed record AnalysisSeries(string Label, string Path, AppFamily App, IReadOnlyList<AnalysisCall> Calls)
{
    public long MaxContext { get; } = Calls.Count == 0 ? 0 : Calls.Max(c => c.Context);

    public long Total { get; } = Calls.Sum(c => c.Context + c.Output);
}

/// <summary>A call that found the cache expired after a pause, and wrote (or read at full price) the whole context again.</summary>
/// <param name="Kept">How long the cache kept what was written before the pause: five minutes, or an hour.</param>
public sealed record CacheRewrite(DateTimeOffset At, TimeSpan Gap, long Tokens, long ContextBefore, string Model, string Series,
    TimeSpan Kept);

/// <summary>A compaction: when, how large the context was before it and after (null when no call has followed yet).</summary>
public sealed record CompactionMark(DateTimeOffset At, bool Marked, long Before, long? After, string Series);

/// <summary>One tool's results: how many, how many bytes in all as written, the largest one.</summary>
public sealed record ToolLoad(string Name, int Results, long Bytes, long Largest)
{
    /// <summary>Bytes of a tool's result to one token, roughly: an estimate, never a count.</summary>
    public const double BytesPerToken = 3.5;

    public long EstimatedTokens => (long)Math.Round(Bytes / BytesPerToken);
}

/// <summary>The conversation's calls added up.</summary>
public sealed record AnalysisTotals(int Calls, long Input, long CacheWrite, long CacheRead, long Output, long MaxContext,
    DateTimeOffset? First, DateTimeOffset? Last)
{
    public long Context => Input + CacheWrite + CacheRead;

    public long Total => Context + Output;

    public double AverageContext => Calls > 0 ? (double)Context / Calls : 0;

    public double CacheHit => Context > 0 ? (double)CacheRead / Context : 0;

    public TimeSpan Duration => First is { } first && Last is { } last && last > first ? last - first : TimeSpan.Zero;
}

/// <summary>
/// One conversation's use of its context, call by call: the lines of calls (the conversation,
/// each subagent or spawned task), the cache rewrites after pauses, the compactions, the tools
/// that filled the context most, and what the numbers say in plain words.
/// </summary>
/// <param name="Unread">Labels of the files that could not be read this time.</param>
public sealed record SessionAnalysis(
    AppFamily App, IReadOnlyList<AnalysisSeries> Series, AnalysisTotals Totals, IReadOnlyList<CacheRewrite> Rewrites,
    IReadOnlyList<CompactionMark> Compactions, IReadOnlyList<ToolLoad> Tools, IReadOnlyList<string> Unread,
    IReadOnlyList<string> Tips)
{
    public long RewrittenTokens => Rewrites.Sum(r => r.Tokens);

    public long ToolBytes => Tools.Sum(t => t.Bytes);
}

/// <summary>
/// Reads one conversation's transcripts on demand — its own and its subagents' or spawned
/// tasks' — and tells how it used its context: how large it grew, how much came from the cache,
/// when the cache had expired over a pause and everything was written again, when it was
/// compacted, and which tools put most into it. The same scanner and the same rules as the usage
/// ledger; nothing is kept once the result is let go.
/// </summary>
public static class SessionAnalyzer
{
    public const string MainLabel = "主线";

    private const string SideLabel = "子代理（记在主记录里）";

    private const string UnknownTool = "（没记下名字的工具）";

    /// <summary>
    /// The conversation's transcript and the ones that belong to it: a Claude conversation's
    /// subagents, the Codex tasks a thread spawned. Gemini and dsh sessions are one file each.
    /// </summary>
    /// <param name="thread">The Codex thread's id; the file read may be this tool's copy, named otherwise.</param>
    /// <param name="recordedPath">The rollout path Codex recorded for the thread.</param>
    public static List<AnalysisFile> FilesFor(string transcriptPath, AppFamily app, CodexPaths? codex = null,
        string? thread = null, string? recordedPath = null, CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transcriptPath);
        var files = new List<AnalysisFile> { new(transcriptPath, app, MainLabel) };
        if (app is not (AppFamily.Claude or AppFamily.Codex)) return files;
        foreach (var (path, label) in ChangeScanner.Related(transcriptPath, app == AppFamily.Codex, codex, cancellation,
                     thread, recordedPath))
            files.Add(new AnalysisFile(path, app, $"{label} · {ShortName(path, app)}"));
        return files;
    }

    /// <summary>A subagent's file name, a spawned task's thread id: short enough for a list.</summary>
    private static string ShortName(string path, AppFamily app)
    {
        var stem = Path.GetFileNameWithoutExtension(TranscriptFiles.WithoutCompression(path));
        if (app == AppFamily.Codex && stem.Length >= 36) return stem[^36..^28];
        return stem.Length > 18 ? stem[..18] : stem;
    }

    /// <param name="files">The conversation's transcript first, then its subagents' or spawned tasks'.</param>
    /// <param name="part">
    /// Which part of the first file is the conversation's own: a resumed or forked copy's history
    /// is read too — it tells what the cache held — but counted where it was made.
    /// </param>
    public static SessionAnalysis Analyze(IReadOnlyList<AnalysisFile> files, OwnPart part = default,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        var reading = new Reading();
        for (var f = 0; f < files.Count; f++)
        {
            cancellation.ThrowIfCancellationRequested();
            var file = files[f];
            UsageScan scan;
            try
            {
                scan = UsageScanner.Scan(file.Path, file.App, 0, null, facts: true, f == 0 ? part : default, null, cancellation);
            }
            catch (Exception ex) when (f > 0 && ex is IOException or UnauthorizedAccessException)
            {
                // One unreadable subagent record: the rest still count.
                reading.Unread.Add(file.Label);
                continue;
            }
            reading.Add(file, f == 0, scan);
        }

        var calls = reading.Series.SelectMany(s => s.Calls).ToList();
        var totals = new AnalysisTotals(calls.Count, calls.Sum(c => c.Input), calls.Sum(c => c.CacheWrite),
            calls.Sum(c => c.CacheRead), calls.Sum(c => c.Output), calls.Count == 0 ? 0 : calls.Max(c => c.Context),
            calls.Count == 0 ? null : calls.Min(c => c.At), calls.Count == 0 ? null : calls.Max(c => c.At));
        var app = files.Count > 0 ? files[0].App : AppFamily.Claude;
        var rewrites = reading.Rewrites.OrderBy(r => r.At).ToList();
        var compactions = reading.Compactions.OrderBy(c => c.At).ToList();
        var tools = reading.Tools.Select(t => new ToolLoad(t.Key, t.Value.Results, t.Value.Bytes, t.Value.Largest))
            .OrderByDescending(t => t.Bytes).ThenBy(t => t.Name, StringComparer.Ordinal).ToList();
        return new SessionAnalysis(app, reading.Series, totals, rewrites, compactions, tools, reading.Unread,
            Tips(app, totals, reading.Series, rewrites, compactions, tools));
    }

    /// <summary>What one analysis gathers, file by file.</summary>
    private sealed class Reading
    {
        public readonly List<AnalysisSeries> Series = [];
        public readonly List<CacheRewrite> Rewrites = [];
        public readonly List<CompactionMark> Compactions = [];
        public readonly Dictionary<string, (int Results, long Bytes, long Largest)> Tools = new(StringComparer.Ordinal);
        public readonly List<string> Unread = [];

        // Calls and tool results counted already: a later file that repeats them does not count them again.
        private readonly HashSet<long> _counted = [];
        private readonly HashSet<string> _results = new(StringComparer.Ordinal);

        public void Add(AnalysisFile file, bool main, UsageScan scan)
        {
            var facts = scan.Facts ?? new UsageFacts(new CallTraits[scan.Calls.Count], []);
            // A response written on several lines is one call, with the largest figures any line gave.
            var merged = new Dictionary<long, UsageCall>();
            foreach (var call in scan.Calls)
                merged[call.Key] = merged.TryGetValue(call.Key, out var known)
                    ? known with
                    {
                        Input = Math.Max(known.Input, call.Input), CacheWrite = Math.Max(known.CacheWrite, call.CacheWrite),
                        CacheRead = Math.Max(known.CacheRead, call.CacheRead), Output = Math.Max(known.Output, call.Output),
                    }
                    : call;

            var line = new ContextSequence(file.App);
            var side = new ContextSequence(file.App);
            var own = new List<AnalysisCall>();
            var others = new List<AnalysisCall>();
            var sideLabel = main ? SideLabel : file.Label + "（旁支）";
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            var taken = new HashSet<long>();
            DateTimeOffset? markedAt = null;
            var events = facts.Events;
            var e = 0;
            for (var i = 0; ; i++)
            {
                for (; e < events.Count && events[e].Before <= i; e++)
                {
                    var noted = events[e];
                    switch (noted.Kind)
                    {
                        case UsageFactKind.Compaction:
                            line.Compacted();
                            markedAt = noted.At ?? markedAt;
                            break;
                        case UsageFactKind.ToolCall when noted is { Id: { } id, Name: { } name }:
                            names[id] = name;
                            break;
                        case UsageFactKind.ToolResult:
                            if (noted.Id is { } result && !_results.Add(result)) break;     // written twice
                            var tool = noted.Name
                                       ?? (noted.Id is { } called && names.TryGetValue(called, out var n) ? n : null)
                                       ?? UnknownTool;
                            var (count, bytes, largest) = Tools.GetValueOrDefault(tool);
                            Tools[tool] = (count + 1, bytes + noted.Bytes, Math.Max(largest, noted.Bytes));
                            break;
                    }
                }
                if (i >= scan.Calls.Count) break;
                var key = scan.Calls[i].Key;
                if (!taken.Add(key)) continue;          // a response's later line
                var traits = facts.Traits[i];
                var mine = line.Follows(traits);
                var call = merged[key];
                var step = (mine ? line : side).Next(call, traits);
                // A copied call stood before the copy's own, and is counted where it was made.
                if ((traits & CallTraits.Copied) != 0 || !_counted.Add(key)) continue;
                var label = mine ? file.Label : sideLabel;
                (mine ? own : others).Add(new AnalysisCall(call.At, call.Model, call.Input, call.CacheWrite, call.CacheRead,
                    call.Output, step.Gap, step.LongGap, step.Compaction, step.ExplicitCompaction, step.Rewritten));
                if (step.Compaction)
                    Compactions.Add(new CompactionMark(call.At, step.ExplicitCompaction, step.ContextBefore, step.Context, label));
                if (step.Rewritten > 0)
                    Rewrites.Add(new CacheRewrite(call.At, step.Gap ?? TimeSpan.Zero, step.Rewritten, step.ContextBefore,
                        call.Model, label, step.Kept));
            }
            // Compacted just now, with no call since: it happened all the same.
            if (line.CompactionPending && own.Count > 0)
                Compactions.Add(new CompactionMark(markedAt ?? own[^1].At, true, own[^1].Context, null, file.Label));

            if (own.Count > 0) Series.Add(new AnalysisSeries(file.Label, file.Path, file.App, own));
            if (others.Count > 0) Series.Add(new AnalysisSeries(sideLabel, file.Path, file.App, others));
        }
    }

    // ------------------------------------------------------------------ in words

    /// <summary>
    /// What the numbers say, in plain words: facts only — how often, how much, what share — and
    /// nothing they do not support.
    /// </summary>
    private static List<string> Tips(AppFamily app, AnalysisTotals totals, IReadOnlyList<AnalysisSeries> series,
        IReadOnlyList<CacheRewrite> rewrites, IReadOnlyList<CompactionMark> compactions, IReadOnlyList<ToolLoad> tools)
    {
        var tips = new List<string>();
        if (totals.Calls == 0) return tips;

        if (rewrites.Count > 0)
        {
            var tokens = rewrites.Sum(r => r.Tokens);
            // Claude keeps what it wrote for an hour when asked to: name the time the pauses outlasted, not always five minutes.
            var kept = rewrites.Select(r => r.Kept).Distinct().ToList();
            var away = kept.Count == 1
                ? $"离开超过 {AnalysisText.Pause(kept[0])}后继续"
                : $"离开超过缓存保留的时间（{string.Join("或 ", kept.Order().Select(AnalysisText.Pause))}）后继续";
            tips.Add(app == AppFamily.Claude
                ? $"{away}，缓存已经过期：整段上下文按缓存写入价重来了 {rewrites.Count} 次，共 {AnalysisText.Tokens(tokens)} token"
                  + (totals.CacheWrite > 0 ? $"，占全部缓存写入的 {AnalysisText.Share(tokens, totals.CacheWrite)}" : "") + "。"
                : $"{away}，缓存已经过期：整段上下文按全价重新读入了 {rewrites.Count} 次，共 {AnalysisText.Tokens(tokens)} token"
                  + (totals.Input > 0 ? $"，占全部未命中缓存输入的 {AnalysisText.Share(tokens, totals.Input)}" : "") + "。");
            if (rewrites.Count > 1)
            {
                var most = rewrites.MaxBy(r => r.Tokens)!;
                tips.Add($"重写最多的一次在 {AnalysisText.When(most.At)}，隔了 {AnalysisText.Pause(most.Gap)}，"
                         + $"重写了 {AnalysisText.Tokens(most.Tokens)} token。");
            }
        }

        if (compactions.Count > 0)
        {
            var marked = compactions.Count(c => c.Marked);
            tips.Add($"上下文压缩了 {compactions.Count} 次"
                     + (marked == compactions.Count ? ""
                         : marked == 0 ? "（按上下文突然减半判断）"
                         : $"（其中 {compactions.Count - marked} 次按上下文突然减半判断）")
                     + $"，压缩前最大 {AnalysisText.Tokens(compactions.Max(c => c.Before))}。");
        }

        var toolBytes = tools.Sum(t => t.Bytes);
        if (toolBytes > 0)
        {
            var top = tools[0];
            tips.Add($"{top.Name} 工具放进了 {AnalysisText.Bytes(top.Bytes)}（约 {AnalysisText.Tokens(top.EstimatedTokens)} token，按字节估算），"
                     + $"占工具结果的 {AnalysisText.Share(top.Bytes, toolBytes)}，共 {top.Results} 次。");
            var big = tools.MaxBy(t => t.Largest)!;
            if (big.Largest >= 50_000)
                tips.Add($"单次最大的工具结果是 {big.Name} 的 {AnalysisText.Bytes(big.Largest)}"
                         + $"（约 {AnalysisText.Tokens((long)Math.Round(big.Largest / ToolLoad.BytesPerToken))} token）。");
        }

        var helpers = series.Where(s => s.Label != MainLabel).ToList();
        if (helpers.Count > 0 && totals.Total > 0)
        {
            var calls = helpers.Sum(s => s.Calls.Count);
            tips.Add($"子代理、子任务共 {calls} 次调用，占这条会话全部 token 的 {AnalysisText.Share(helpers.Sum(s => s.Total), totals.Total)}。");
        }

        if (totals.Calls >= 10 && totals.Context > 0)
            tips.Add($"读入的上下文里有 {AnalysisText.Share(totals.CacheRead, totals.Context)} 来自缓存。");

        if (rewrites.Count == 0 && compactions.Count == 0)
            tips.Add("没有发现缓存过期后的重写，也没有压缩。");
        return tips;
    }
}

/// <summary>Numbers the way the analysis says them: 1.23 亿, 4.5 万, 3 分钟, 12%.</summary>
public static class AnalysisText
{
    public static string Tokens(long n) => n switch
    {
        >= 100_000_000 => string.Create(CultureInfo.InvariantCulture, $"{n / 1e8:0.##} 亿"),
        >= 10_000 => string.Create(CultureInfo.InvariantCulture, $"{n / 1e4:0.#} 万"),
        _ => n.ToString("N0", CultureInfo.InvariantCulture),
    };

    public static string Bytes(long n) => n switch
    {
        >= 100_000_000 => string.Create(CultureInfo.InvariantCulture, $"{n / 1e8:0.##} 亿字节"),
        >= 10_000 => string.Create(CultureInfo.InvariantCulture, $"{n / 1e4:0.#} 万字节"),
        _ => n.ToString("N0", CultureInfo.InvariantCulture) + " 字节",
    };

    public static string Share(long part, long whole) =>
        whole <= 0 ? "0%" : string.Create(CultureInfo.InvariantCulture, $"{Math.Round(100.0 * part / whole):0}%");

    public static string Pause(TimeSpan gap) => gap.TotalMinutes switch
    {
        < 1 => $"{Math.Max(0, (int)gap.TotalSeconds)} 秒",
        < 60 => $"{(int)gap.TotalMinutes} 分钟",
        < 48 * 60 => string.Create(CultureInfo.InvariantCulture, $"{gap.TotalHours:0.#} 小时"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{gap.TotalDays:0.#} 天"),
    };

    public static string When(DateTimeOffset at) =>
        at.ToLocalTime().ToString("M月d日 HH:mm", CultureInfo.InvariantCulture);
}
