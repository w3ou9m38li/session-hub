using System.Text;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Catalog;
using ClaudeLauncher.Infrastructure.Search;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>Tests that set <see cref="TranscriptLocator.Archive"/>, which every lookup reads: one at a time.</summary>
[CollectionDefinition(nameof(LocatorArchive), DisableParallelization = true)]
public sealed class LocatorArchive;

/// <summary>
/// This tool's own copies of the transcripts: made, grown and replaced as the transcripts are,
/// never from a line still being written, and read in their place once an app deletes them —
/// or keeps them only compressed.
/// </summary>
[Collection(nameof(LocatorArchive))]
public sealed class TranscriptArchiveTests : IDisposable
{
    private readonly string _dir;
    private readonly string _apps;
    private readonly TranscriptArchive _archive;

    public TranscriptArchiveTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "launcher-archive", Guid.NewGuid().ToString("n"));
        _apps = Path.Combine(_dir, "apps");
        Directory.CreateDirectory(_apps);
        _archive = new TranscriptArchive(Path.Combine(_dir, "data", "archive"));
    }

    private static string Line(int n) => "{\"type\":\"user\",\"n\":" + n + ",\"text\":\"" + new string('x', 50) + "\"}\n";

    private string Transcript(string name, string text, DateTime? written = null)
    {
        var path = Path.Combine(_apps, name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, written ?? DateTime.UtcNow.AddMinutes(-10));
        return path;
    }

    private TranscriptArchive.UpdateRun Keep(params TranscriptArchive.Source[] sources) =>
        _archive.Update(sources, TimeSpan.FromMinutes(1));

    [Fact]
    public void ACopyIsMadeThenGrownThenMadeAgainWhenTheTranscriptWasRewritten()
    {
        var path = Transcript("a.jsonl", Line(1) + Line(2));
        var source = new TranscriptArchive.Source(path, "claude", "a");
        var before = File.ReadAllBytes(path);

        var first = Keep(source);
        Assert.Equal(1, first.Copied);
        var copy = _archive.CopyOf("claude", "a")!;
        Assert.Equal(before, File.ReadAllBytes(copy));
        Assert.Equal(before, File.ReadAllBytes(path));                     // the original only read

        Assert.False(Keep(source).ChangedAnything);                        // nothing new: nothing written

        // Grown, with its last line still being written: the complete one is added, not the half.
        File.AppendAllText(path, Line(3) + "{\"type\":\"assist");
        var grown = Keep(source);
        Assert.Equal(1, grown.Grown);
        Assert.Equal(Line(1) + Line(2) + Line(3), File.ReadAllText(copy));

        File.AppendAllText(path, "ant\"}\n");
        Keep(source);
        Assert.Equal(Line(1) + Line(2) + Line(3) + "{\"type\":\"assistant\"}\n", File.ReadAllText(copy));

        // Written again with other content (and longer): copied whole, not appended to.
        Transcript("a.jsonl", Line(7) + Line(8) + Line(9) + Line(10) + Line(11));
        Assert.Equal(1, Keep(source).Copied);
        Assert.Equal(File.ReadAllText(path), File.ReadAllText(copy));
    }

    [Fact]
    public void ADocumentWrittenAgainWholeIsCopiedOnceLeftAlone()
    {
        var path = Transcript("session-1.json", "{\"sessionId\":\"g\",\"messages\":[]}", written: DateTime.UtcNow);
        var source = new TranscriptArchive.Source(path, "gemini", "g");

        Keep(source);
        Assert.Null(_archive.CopyOf("gemini", "g"));                       // still being written to

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        Keep(source);
        Assert.Equal(File.ReadAllText(path), File.ReadAllText(_archive.CopyOf("gemini", "g")!));
    }

    [Fact]
    public void ACompressedRolloutIsUnpackedIntoItsCopy()
    {
        var text = Line(1) + Line(2) + Line(3);
        var packed = Path.Combine(_apps, "rollout-2026-04-26T10-00-00-0b1b2c3d-0000-4000-8000-00000000000b.jsonl.zst");
        using (var output = File.Create(packed))
        using (var zstd = new ZstdSharp.CompressionStream(output))
            zstd.Write(Encoding.UTF8.GetBytes(text));

        var copy = _archive.Unpacked(packed, "codex", "0b1b2c3d-0000-4000-8000-00000000000b");

        Assert.NotNull(copy);
        Assert.Equal(text, File.ReadAllText(copy));
        Assert.Equal(copy, _archive.Unpacked(packed, "codex", "0b1b2c3d-0000-4000-8000-00000000000b"));   // once
        using var reader = new StreamReader(TranscriptFiles.OpenRead(packed));
        Assert.Equal(text, reader.ReadToEnd());
    }

    [Fact]
    public void TheOldestTranscriptsAreCopiedFirstAndARunOutOfTimeCarriesOn()
    {
        var older = Transcript("old.jsonl", Line(1), DateTime.UtcNow.AddDays(-40));
        var newer = Transcript("new.jsonl", Line(2), DateTime.UtcNow.AddDays(-1));

        var none = _archive.Update(
            [new(newer, "claude", "new"), new(older, "claude", "old")], TimeSpan.Zero);
        Assert.Equal(2, none.Pending);

        var all = Keep(new(newer, "claude", "new"), new(older, "claude", "old"));
        Assert.Equal(2, all.Copied);
        Assert.Equal(0, all.Pending);
        Assert.Equal((2, (long)(Line(1).Length + Line(2).Length)), _archive.Size());
    }

    [Fact]
    public void AGoneTranscriptIsReadFromItsCopyAndADeletedOneIsNot()
    {
        var config = Path.Combine(_dir, "claude");
        var project = Path.Combine(config, "projects", "D--work");
        Directory.CreateDirectory(project);
        var id = "11111111-0000-4000-8000-000000000001";
        var path = Path.Combine(project, id + ".jsonl");
        File.WriteAllText(path, Line(1));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-10));
        var row = new SessionMetadata
        {
            Key = new ConversationKey(ConversationKey.CliCode, "acc", "org", id),
            Title = "t", SearchKey = "t", SourceRevision = "1",
        };
        var identity = TranscriptArchive.IdentityOf(row)!.Value;
        Assert.Equal(("claude", id), identity);

        TranscriptLocator.Archive = _archive;
        try
        {
            Keep(new TranscriptArchive.Source(path, identity.Family, identity.Conversation));
            Assert.Equal(path, TranscriptLocator.Find(row, config));       // the original while it is there

            File.Delete(path);                                              // cleaned up by the app
            var copy = TranscriptLocator.Find(row, config);
            Assert.Equal(_archive.CopyOf("claude", id), copy);
            Assert.Equal(Line(1), File.ReadAllText(copy!));
            Assert.Equal(copy, Assert.Single(TranscriptLocator.FindAll([row], config, CodexPaths.FromHome(Path.Combine(_dir, "codex")))).Path);

            _archive.Remove("claude", id);                                  // deleted here by the user
            Assert.Null(TranscriptLocator.Find(row, config));
        }
        finally
        {
            TranscriptLocator.Archive = null;
        }
    }

    [Fact]
    public void WhatWasSaidStaysSearchableWhileTheConversationIsListed()
    {
        using var index = new ContentIndex(Path.Combine(_dir, "search-index.sqlite"));
        var path = Transcript("s.jsonl",
            "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"候选基因的位置\"}]}}\n");
        Assert.Equal(1, index.Update([new ContentIndex.Source("cli|a|o|s", path)]).Indexed);

        // The file is gone but the conversation still listed: what was said stays.
        var kept = index.Update([], listed: new HashSet<string>(["cli|a|o|s"]));
        Assert.Equal(0, kept.Removed);
        Assert.Equal(1, index.Count());

        // No longer listed at all (deleted by the user): it leaves.
        Assert.Equal(1, index.Update([], listed: new HashSet<string>()).Removed);
    }

    public void Dispose()
    {
        _archive.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
