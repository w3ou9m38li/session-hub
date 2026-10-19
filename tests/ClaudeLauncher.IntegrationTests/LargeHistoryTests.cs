using System.Diagnostics;
using System.Text;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Catalog;
using ClaudeLauncher.Infrastructure.Discovery;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// Thousands of conversations: a launch does not reopen every unchanged transcript, a first
/// launch shows the newest before the rest is read, and writing a sync does not slow down
/// with the size of the list.
/// </summary>
public sealed class LargeHistoryTests : IDisposable
{
    private static readonly DateTime Written = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);

    private readonly string _dir;
    private readonly string _config;
    private readonly string _project;

    public LargeHistoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "launcher-large", Guid.NewGuid().ToString("n"));
        _config = Path.Combine(_dir, "claude");
        _project = Path.Combine(_config, "projects", "G--demo");
        Directory.CreateDirectory(_project);
    }

    private string Transcript(int n, string title, DateTime? written = null)
    {
        var path = Path.Combine(_project, $"{n:D8}-0000-0000-0000-000000000000.jsonl");
        File.WriteAllText(path, "{\"type\":\"custom-title\",\"customTitle\":\"" + title + "\",\"cwd\":\"G:\\\\demo\"}\n",
            new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, written ?? Written);
        return path;
    }

    private SqliteCatalog Catalog()
    {
        var catalog = new SqliteCatalog(Path.Combine(_dir, "catalog.sqlite"));
        catalog.Initialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CatalogSchema.sql")));
        return catalog;
    }

    [Fact]
    public void WhatWasReadFromEachTranscriptIsKeptBetweenRuns()
    {
        var file = Path.Combine(_dir, "probe-cache.json");
        var held = Transcript(1, "标题甲");
        Transcript(2, "标题乙");
        var first = new CliProbeCache();
        new CliMetadataReader(_config, cache: first).Read("acc", "org").ToList();
        Assert.True(first.Changed);
        Assert.True(first.Save(file));
        Assert.False(first.Changed);

        // The next run, with a transcript held open by its app so it cannot be read: unchanged, it is not opened.
        var next = CliProbeCache.Load(file);
        Assert.Equal(2, next.Count);
        using (new FileStream(held, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var titles = new CliMetadataReader(_config, cache: next).Read("acc", "org").Select(r => r.Title).ToList();
            Assert.Contains("标题甲", titles);
            Assert.Contains("标题乙", titles);
        }
        Assert.False(next.Changed);

        // Changed since: read again.
        Transcript(2, "改过的标题", Written.AddMinutes(5));
        Assert.Contains("改过的标题", new CliMetadataReader(_config, cache: next).Read("acc", "org").Select(r => r.Title));
        Assert.True(next.Changed);
    }

    [Fact]
    public void ASavedCacheFromOtherRulesOrDamagedReadsAsEmpty()
    {
        var file = Path.Combine(_dir, "probe-cache.json");
        Transcript(1, "标题甲");
        var cache = new CliProbeCache();
        new CliMetadataReader(_config, cache: cache).Read("acc", "org").ToList();
        cache.Save(file);
        Assert.Equal(1, CliProbeCache.Load(file).Count);

        File.WriteAllText(file, File.ReadAllText(file).Replace($"\"Rules\":{CliProbeCache.ProbeRules}", "\"Rules\":-1"));
        Assert.Equal(0, CliProbeCache.Load(file).Count);
        File.WriteAllText(file, "{ not json");
        Assert.Equal(0, CliProbeCache.Load(file).Count);
        Assert.Equal(0, CliProbeCache.Load(Path.Combine(_dir, "missing.json")).Count);
    }

    [Fact]
    public void WhatTheFirstTitleRuleFoundIsReadAgainWhereALaterTitleCanChangeIt()
    {
        static string Line(string json) => json + "\n";
        string Write(string name, string text)
        {
            var path = Path.Combine(_project, name + ".jsonl");
            File.WriteAllText(path, text, new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(path, Written);
            return path;
        }
        var filler = string.Concat(Enumerable.Repeat(
            Line("{\"type\":\"assistant\",\"message\":{\"content\":\"" + new string('x', 1000) + "\"}}"), 400));
        // Renamed far into a long conversation, renamed twice in a short one, never named.
        var renamedLate = Write("aaaaaaaa-0000-0000-0000-00000000000a",
            Line("{\"type\":\"custom-title\",\"customTitle\":\"最初的名字\"}") + filler
            + Line("{\"type\":\"custom-title\",\"customTitle\":\"后来起的名字\"}"));
        var renamedTwice = Write("bbbbbbbb-0000-0000-0000-00000000000b",
            Line("{\"type\":\"custom-title\",\"customTitle\":\"最初的名字\"}")
            + Line("{\"type\":\"custom-title\",\"customTitle\":\"改过的名字\"}"));
        var untitled = Write("cccccccc-0000-0000-0000-00000000000c",
            Line("{\"type\":\"user\",\"message\":{\"content\":\"第一条真实提问\"}}"));

        // Saved by a version that kept the first title it met and never looked at a transcript's end.
        // The untitled one's entry says something else, to tell whether it is still used.
        object Saved(string path, string title, string source) => new
        {
            Path = path, Length = new FileInfo(path).Length, Written = Written.Ticks,
            Title = title, TitleSource = source, Cwd = (string?)null, Truncated = false,
            HeadSession = (string?)null, SaidAnything = (bool?)null,
        };
        var file = Path.Combine(_dir, "probe-cache.json");
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new
        {
            Rules = 1,
            Probes = new[]
            {
                Saved(renamedLate, "最初的名字", "custom-title"),
                Saved(renamedTwice, "最初的名字", "custom-title"),
                Saved(untitled, "缓存里的标题", "first-message"),
            },
        }));

        var cache = CliProbeCache.Load(file);
        Assert.Equal(1, cache.Count);
        var titles = new CliMetadataReader(_config, cache: cache).Read("acc", "org")
            .ToDictionary(r => r.Key.NativeSessionId, r => r.Title);
        Assert.Equal("后来起的名字", titles["aaaaaaaa-0000-0000-0000-00000000000a"]);
        Assert.Equal("改过的名字", titles["bbbbbbbb-0000-0000-0000-00000000000b"]);
        // Short and untitled: nothing a later title can change, so not opened again.
        Assert.Equal("缓存里的标题", titles["cccccccc-0000-0000-0000-00000000000c"]);

        // Saved under the current rules: the next run takes all of it.
        Assert.True(cache.Changed);
        Assert.True(cache.Save(file));
        Assert.Equal(3, CliProbeCache.Load(file).Count);
    }

    [Fact]
    public void AFirstLookReadsTheNewestAndMarksNothingGone()
    {
        for (var i = 1; i <= 5; i++) Transcript(i, "第 " + i + " 条", Written.AddHours(i));
        using var catalog = Catalog();
        var sync = new CatalogSyncService(catalog, _config);
        var claude = new ClaudeInstallation { Found = false };
        var codex = CodexPaths.FromHome(Path.Combine(_dir, "codex"));

        Assert.True(catalog.IsEmpty());
        Assert.Equal(5, sync.Sync(claude, codex).RowsChanged);
        Assert.False(catalog.IsEmpty());

        // Only the two written last: a partial look, so the other three are not marked gone.
        var first = sync.Read(claude, codex, newestTranscripts: 2);
        Assert.Equal(["第 4 条", "第 5 条"],
            first.Rows.Where(r => r.Key.Backend == ConversationKey.CliCode).Select(r => r.Title).Order().ToArray());
        Assert.DoesNotContain(ConversationKey.CliCode, first.Complete);
        Assert.Equal(SourceCoverage.Partial, first.Tallies.Single(t => t.Backend == ConversationKey.CliCode).Coverage);
        Assert.Equal(0, sync.Apply(first).RowsRemoved);
        Assert.Equal(5, catalog.Page(Visibility.Active, null, null, 10).Rows.Count(r => r.SourceGoneUtc is null));
    }

    [Fact]
    public void WritingASyncStaysQuickWithThousandsOfConversations()
    {
        using var catalog = Catalog();
        var rows = new List<SessionMetadata>();
        for (var i = 0; i < 5000; i++)
        {
            var id = $"cli-{i:D5}";
            rows.Add(new SessionMetadata
            {
                Key = new ConversationKey(ConversationKey.CliCode, "acc", "org", id),
                Title = id, SearchKey = id, SourceRevision = "1",
                LastActivityUtc = DateTimeOffset.UtcNow.AddMinutes(-i),
                NativeArchiveState = NativeArchiveState.Unsupported,
                // A few are folded into desktop sessions.
                MergedInto = i % 500 == 0 ? $"local_{i}" : null,
            });
            if (i % 500 == 0)
                rows.Add(new SessionMetadata
                {
                    Key = new ConversationKey(ConversationKey.DesktopCode, "acc", "org", $"local_{i}"),
                    Title = id, SearchKey = id, SourceRevision = "1", CliSessionId = id,
                    NativeArchiveState = NativeArchiveState.Active,
                });
        }
        catalog.UpsertRange(rows);

        var watch = Stopwatch.StartNew();
        catalog.AdoptMergedPreferences();
        catalog.UpsertRange(rows);
        catalog.MarkGone(ConversationKey.CliCode, rows.Select(r => r.Key));
        // Was eighteen seconds at three thousand rows: a lookup that scanned the table once per row.
        Assert.True(watch.ElapsedMilliseconds < 3000, $"writing took {watch.ElapsedMilliseconds} ms");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
