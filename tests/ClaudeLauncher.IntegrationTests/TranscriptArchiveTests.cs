using System.Globalization;
using System.Text;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Backends;
using ClaudeLauncher.Infrastructure.Catalog;
using ClaudeLauncher.Infrastructure.Search;
using ClaudeLauncher.Infrastructure.Usage;
using Microsoft.Data.Sqlite;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>Tests that set <see cref="TranscriptLocator.Archive"/>, which every lookup reads: one at a time.</summary>
[CollectionDefinition(nameof(LocatorArchive), DisableParallelization = true)]
public sealed class LocatorArchive;

/// <summary>
/// This tool's own copies of the transcripts: made, grown and replaced as the transcripts are,
/// never from a line still being written, and read in their place once an app deletes them —
/// or keeps them only compressed. Copies are packed, and read as the transcripts they hold;
/// those made before copies were packed are packed, checked, and only then let go of.
/// </summary>
[Collection(nameof(LocatorArchive))]
public sealed class TranscriptArchiveTests : IDisposable
{
    private readonly string _dir;
    private readonly string _apps;
    private readonly string _root;
    private readonly TranscriptArchive _archive;

    public TranscriptArchiveTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "launcher-archive", Guid.NewGuid().ToString("n"));
        _apps = Path.Combine(_dir, "apps");
        Directory.CreateDirectory(_apps);
        _root = Path.Combine(_dir, "data", "archive");
        _archive = new TranscriptArchive(_root);
    }

    private static string Line(int n) => "{\"type\":\"user\",\"n\":" + n + ",\"text\":\"" + new string('x', 50) + "\"}\n";

    private string Transcript(string name, string text, DateTime? written = null)
    {
        var path = Path.Combine(_apps, name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, written ?? DateTime.UtcNow.AddMinutes(-10));
        return path;
    }

    /// <summary>What a copy holds: the transcript as the app wrote it.</summary>
    private static byte[] Held(string copy)
    {
        using var stream = TranscriptFiles.OpenRead(copy);
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static string HeldText(string copy) => Encoding.UTF8.GetString(Held(copy));

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
        Assert.EndsWith(".jsonl" + PackedCopy.Suffix, copy);
        Assert.NotNull(PackedCopy.Stat(copy));                              // packed, not plain
        Assert.Equal(before, Held(copy));
        Assert.Equal(before, File.ReadAllBytes(path));                     // the original only read

        Assert.False(Keep(source).ChangedAnything);                        // nothing new: nothing written

        // Grown, with its last line still being written: the complete one is added, not the half.
        File.AppendAllText(path, Line(3) + "{\"type\":\"assist");
        var grown = Keep(source);
        Assert.Equal(1, grown.Grown);
        Assert.Equal(Line(1) + Line(2) + Line(3), HeldText(copy));

        File.AppendAllText(path, "ant\"}\n");
        Keep(source);
        Assert.Equal(Line(1) + Line(2) + Line(3) + "{\"type\":\"assistant\"}\n", HeldText(copy));

        // Written again with other content (and longer): copied whole, not appended to.
        Transcript("a.jsonl", Line(7) + Line(8) + Line(9) + Line(10) + Line(11));
        Assert.Equal(1, Keep(source).Copied);
        Assert.Equal(File.ReadAllText(path), HeldText(copy));
    }

    [Fact]
    public void ALineStillBeingWrittenWhenATranscriptGrowsWithoutANewLineAddsNothing()
    {
        var path = Transcript("a.jsonl", Line(1));
        var source = new TranscriptArchive.Source(path, "claude", "a");
        Keep(source);
        var copy = _archive.CopyOf("claude", "a")!;
        var packed = File.ReadAllBytes(copy);

        File.AppendAllText(path, "{\"type\":\"user\",\"te");
        Assert.False(Keep(source).ChangedAnything);
        Assert.Equal(packed, File.ReadAllBytes(copy));                     // not even rewritten
        Assert.False(Keep(source).ChangedAnything);                        // and not looked at again until it changes

        File.AppendAllText(path, "xt\":\"y\"}\n");
        Assert.Equal(1, Keep(source).Grown);
        Assert.Equal(File.ReadAllText(path), HeldText(copy));
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
        var copy = _archive.CopyOf("gemini", "g")!;
        Assert.Equal(File.ReadAllText(path), HeldText(copy));
        Assert.True(GeminiSession.IsSessionFile(copy));                     // read as the session it holds
        Assert.True(WholeTranscript.Is(copy));
    }

    [Fact]
    public void ACompressedRolloutIsReadThroughItsCopy()
    {
        var text = Line(1) + Line(2) + Line(3);
        var packed = Path.Combine(_apps, "rollout-2026-04-26T10-00-00-0b1b2c3d-0000-4000-8000-00000000000b.jsonl.zst");
        using (var output = File.Create(packed))
        using (var zstd = new ZstdSharp.CompressionStream(output))
            zstd.Write(Encoding.UTF8.GetBytes(text));
        Assert.Null(PackedCopy.Open(packed));                               // someone else's Zstandard: no seek table

        var copy = _archive.Readable(packed, "codex", "0b1b2c3d-0000-4000-8000-00000000000b");

        Assert.NotNull(copy);
        Assert.Equal(text, HeldText(copy));
        Assert.Equal(copy, _archive.Readable(packed, "codex", "0b1b2c3d-0000-4000-8000-00000000000b"));   // once
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
        var size = _archive.Size();
        Assert.Equal(2, size.Copies);
        Assert.Equal(Line(1).Length + Line(2).Length, size.Bytes);
        Assert.Equal(new FileInfo(_archive.CopyOf("claude", "old")!).Length + new FileInfo(_archive.CopyOf("claude", "new")!).Length,
            size.Stored);
        Assert.Equal(0, size.Plain);
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
            Assert.Equal(Line(1), HeldText(copy!));
            Assert.Equal(copy, Assert.Single(TranscriptLocator.FindAll([row], config, CodexPaths.FromHome(Path.Combine(_dir, "codex")))).Path);

            _archive.Remove("claude", id);                                  // deleted here by the user
            Assert.Null(TranscriptLocator.Find(row, config));
            Assert.False(File.Exists(copy));
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

    // ------------------------------------------------------------ packed copies

    private static string Said(string id, string role, string text, long output = 0) =>
        "{\"type\":\"" + role + "\",\"timestamp\":\"2026-10-01T08:00:0" + id + "Z\",\"uuid\":\"u" + id + "\",\"requestId\":\"req_" + id
        + "\",\"message\":{\"model\":\"claude-opus-5\",\"id\":\"msg_" + id + "\",\"role\":\"" + role
        + "\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]"
        + (output > 0 ? ",\"usage\":{\"input_tokens\":10,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":" + output + "}" : "")
        + "}}\n";

    [Fact]
    public void TheViewTheIndexAndTheUsageCountReadAPackedCopyAsTheTranscript()
    {
        var path = Transcript("t.jsonl",
            Said("1", "user", "候选基因在哪条染色体上") + Said("2", "assistant", "在 7A 上", output: 42)
            + Said("3", "user", "再看看 2B") + Said("4", "assistant", "2B 上也有一个", output: 17));
        Keep(new TranscriptArchive.Source(path, "claude", "t"));
        var copy = _archive.CopyOf("claude", "t")!;

        Assert.Equal(new FileInfo(path).Length, TranscriptFiles.Stat(copy)!.Value.Length);
        using (var reader = new TranscriptReader(copy))
            Assert.Equal(
                new TranscriptReader(path).Read().Entries.Select(e => e.Text),
                reader.Read().Entries.Select(e => e.Text));
        Assert.Equal(
            TranscriptReader.ReadTail(path, 2).Entries.Select(e => e.Text),
            TranscriptReader.ReadTail(copy, 2).Entries.Select(e => e.Text));
        var (fromHalf, next) = TranscriptReader.ReadDialogueFrom(copy, new FileInfo(path).Length / 2);
        Assert.Equal(TranscriptReader.ReadDialogueFrom(path, new FileInfo(path).Length / 2).Entries.Select(e => e.Text),
            fromHalf.Select(e => e.Text));
        Assert.Equal(new FileInfo(path).Length, next);

        var counted = UsageScanner.Scan(copy, AppFamily.Claude, 0, null);
        Assert.Equal(UsageScanner.Scan(path, AppFamily.Claude, 0, null).Calls, counted.Calls);
        Assert.Equal(59, counted.Calls.Sum(c => c.Output));

        using var index = new ContentIndex(Path.Combine(_dir, "search-index.sqlite"));
        Assert.Equal(1, index.Update([new ContentIndex.Source("cli|a|o|t", copy)]).Indexed);
        Assert.NotEmpty(index.Hits("cli|a|o|t", "染色体"));
    }

    [Fact]
    public void ADamagedCopyIsMadeAgainWholeFromItsTranscript()
    {
        var path = Transcript("d.jsonl", string.Concat(Enumerable.Range(1, 40).Select(Line)));
        var source = new TranscriptArchive.Source(path, "claude", "d");
        Keep(source);
        var copy = _archive.CopyOf("claude", "d")!;
        var bytes = File.ReadAllBytes(copy);
        bytes[bytes.Length / 3] ^= 0x5A;                                    // inside the frame, before the table
        File.WriteAllBytes(copy, bytes);
        Assert.Throws<IOException>(() => Held(copy));

        File.AppendAllText(path, Line(41));
        Assert.Equal(1, Keep(source).Copied);                               // not appended to: made again
        Assert.Equal(File.ReadAllText(path), HeldText(copy));
    }

    /// <summary>An archive as an earlier version left it: plain copies, and no column for a copy's tail.</summary>
    private static void OldArchive(string root, params (string Family, string Conversation, string Source, string Text)[] copies)
    {
        Directory.CreateDirectory(root);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "archive.sqlite")};Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE copies (
              family TEXT NOT NULL, conversation TEXT NOT NULL, source TEXT NOT NULL, copy TEXT NOT NULL,
              source_length INTEGER NOT NULL, source_written INTEGER NOT NULL, copied_length INTEGER NOT NULL,
              copied_utc TEXT NOT NULL, PRIMARY KEY(family, conversation));
            """;
        cmd.ExecuteNonQuery();
        foreach (var (family, conversation, source, text) in copies)
        {
            var folder = Path.Combine(root, "copies", family);
            Directory.CreateDirectory(folder);
            var copy = Path.Combine(folder, conversation + ".jsonl");
            File.WriteAllText(copy, text, new UTF8Encoding(false));
            var info = new FileInfo(source);
            cmd.CommandText = """
                INSERT INTO copies VALUES($f, $c, $s, $copy, $length, $written, $copied, $at);
                """;
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$f", family);
            cmd.Parameters.AddWithValue("$c", conversation);
            cmd.Parameters.AddWithValue("$s", source);
            cmd.Parameters.AddWithValue("$copy", copy);
            cmd.Parameters.AddWithValue("$length", info.Exists ? info.Length : text.Length);
            cmd.Parameters.AddWithValue("$written", info.Exists ? info.LastWriteTimeUtc.Ticks : 0L);
            cmd.Parameters.AddWithValue("$copied", (long)text.Length);
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }
    }

    [Fact]
    public void CopiesFromBeforeCopiesWerePackedArePackedThenCheckedThenTheirPlainFilesGo()
    {
        var root = Path.Combine(_dir, "earlier", "archive");
        var kept = Transcript("kept.jsonl", Line(1) + Line(2));
        var gone = Path.Combine(_apps, "gone.jsonl");                         // the app deleted it long ago
        OldArchive(root,
            ("claude", "kept", kept, Line(1) + Line(2)),
            ("codex", "gone", gone, Line(5) + Line(6) + Line(7)));
        var plainKept = Path.Combine(root, "copies", "claude", "kept.jsonl");
        var plainGone = Path.Combine(root, "copies", "codex", "gone.jsonl");

        using var archive = new TranscriptArchive(root);
        Assert.Equal(2, archive.Size().Plain);

        // No time to spare: nothing packed, and what is left is said.
        var none = archive.Update([new(kept, "claude", "kept")], TimeSpan.Zero);
        Assert.Equal((0, 2), (none.Packed, none.Pending));
        Assert.True(File.Exists(plainKept) && File.Exists(plainGone));

        var run = archive.Update([new(kept, "claude", "kept")], TimeSpan.FromMinutes(1));
        Assert.Equal((2, 0), (run.Packed, run.Pending));
        Assert.False(File.Exists(plainKept));
        Assert.False(File.Exists(plainGone));
        Assert.Equal(plainKept + PackedCopy.Suffix, archive.CopyOf("claude", "kept"));
        Assert.Equal(Line(1) + Line(2), HeldText(archive.CopyOf("claude", "kept")!));
        Assert.Equal(Line(5) + Line(6) + Line(7), HeldText(archive.CopyOf("codex", "gone")!));   // its only record
        Assert.Equal(0, archive.Size().Plain);

        // Packed with the hash of its end: grown from here on like any other copy.
        File.AppendAllText(kept, Line(3));
        Assert.Equal(1, archive.Update([new(kept, "claude", "kept")], TimeSpan.FromMinutes(1)).Grown);
        Assert.Equal(Line(1) + Line(2) + Line(3), HeldText(archive.CopyOf("claude", "kept")!));
    }

    [Fact]
    public void WhatWasReadFromAPlainCopyHoldsForItsPackedCopyWithoutReadingItAgain()
    {
        var root = Path.Combine(_dir, "earlier", "archive");
        var gone = Path.Combine(_apps, "gone.jsonl");
        var text = Said("1", "user", "候选基因在哪条染色体上") + Said("2", "assistant", "在 7A 上", output: 42);
        OldArchive(root, ("claude", "g", gone, text));
        var plain = Path.Combine(root, "copies", "claude", "g.jsonl");
        const string Conversation = "cli|a|o|g";

        using var index = new ContentIndex(Path.Combine(_dir, "search-index.sqlite"));
        using var ledger = new UsageLedger(Path.Combine(_dir, "usage.sqlite"));
        Assert.Equal(1, index.Update([new ContentIndex.Source(Conversation, plain)]).Indexed);
        Assert.Equal(1, ledger.Update([new UsageLedger.Source(Conversation, plain, AppFamily.Claude)]).Read);

        using (var archive = new TranscriptArchive(root))
        {
            Assert.Equal(1, archive.Update([], TimeSpan.FromMinutes(1)).Packed);
            var packed = archive.CopyOf("claude", "g")!;
            Assert.Equal(plain + PackedCopy.Suffix, packed);

            // Only the name changed: nothing is read again, and what was found stays found.
            Assert.Equal(0, index.Update([new ContentIndex.Source(Conversation, packed)]).Indexed);
            Assert.NotEmpty(index.Hits(Conversation, "染色体"));
            Assert.Equal(0, ledger.Update([new UsageLedger.Source(Conversation, packed, AppFamily.Claude)]).Read);
            Assert.Equal(42, ledger.Totals(Conversation)?.Output);
            Assert.Equal(0, index.Update([new ContentIndex.Source(Conversation, packed)]).Indexed);     // and it stays settled
            Assert.Equal(0, ledger.Update([new UsageLedger.Source(Conversation, packed, AppFamily.Claude)]).Read);
        }
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void RecordsFromADataRootThatMovedFindTheirCopiesHereAndNothingOutsideIsTouched()
    {
        // Records written under an earlier root; the folder copied here with the root, the old one still there.
        var earlier = Path.Combine(_dir, "earlier-root", "archive");
        var kept = Transcript("m.jsonl", Line(1) + Line(2));
        OldArchive(earlier,
            ("claude", "m", kept, Line(1) + Line(2)),
            ("claude", "n", Path.Combine(_apps, "gone.jsonl"), Line(4)));
        var here = Path.Combine(_dir, "this-root", "archive");
        foreach (var file in Directory.EnumerateFiles(earlier, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(here, Path.GetRelativePath(earlier, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }
        File.Delete(Path.Combine(here, "copies", "claude", "n.jsonl"));      // this one only in the earlier root

        using var archive = new TranscriptArchive(here);
        Assert.Equal(Path.Combine(here, "copies", "claude", "m.jsonl"), archive.CopyOf("claude", "m"));

        var run = archive.Update([new(kept, "claude", "m")], TimeSpan.FromMinutes(1));
        Assert.Equal(2, run.Packed);
        foreach (var conversation in new[] { "m", "n" })
            Assert.StartsWith(Path.Combine(here, "copies", "claude") + Path.DirectorySeparatorChar, archive.CopyOf("claude", conversation));
        Assert.Equal(Line(1) + Line(2), HeldText(archive.CopyOf("claude", "m")!));
        Assert.Equal(Line(4), HeldText(archive.CopyOf("claude", "n")!));
        Assert.False(File.Exists(Path.Combine(here, "copies", "claude", "m.jsonl")));   // its own plain copy, packed: gone
        // The earlier root's files are not this archive's: read, never changed or deleted.
        Assert.Equal(Line(1) + Line(2), File.ReadAllText(Path.Combine(earlier, "copies", "claude", "m.jsonl")));
        Assert.Equal(Line(4), File.ReadAllText(Path.Combine(earlier, "copies", "claude", "n.jsonl")));

        archive.Remove("claude", "n");
        Assert.True(File.Exists(Path.Combine(earlier, "copies", "claude", "n.jsonl")));
    }

    [Fact]
    public void WhatAnEarlierPassLeftBehindIsClearedAwayAndNothingElse()
    {
        var path = Transcript("a.jsonl", Line(1));
        Keep(new TranscriptArchive.Source(path, "claude", "a"));
        var copy = _archive.CopyOf("claude", "a")!;
        var folder = Path.GetDirectoryName(copy)!;
        var plain = copy[..^PackedCopy.Suffix.Length];
        File.WriteAllText(plain, Line(1));                                  // a plain copy that could not be deleted once packed
        File.WriteAllText(copy + ".part", "half");                          // a copy half written when the app was closed
        var stranger = Path.Combine(folder, "someone-elses.jsonl");
        File.WriteAllText(stranger, Line(9));                               // not this tool's to judge: stays
        _archive.Dispose();

        using var again = new TranscriptArchive(_root);
        again.Update([], TimeSpan.FromMinutes(1));
        Assert.False(File.Exists(plain));
        Assert.False(File.Exists(copy + ".part"));
        Assert.True(File.Exists(stranger));
        Assert.Equal(Line(1), HeldText(copy));
    }

    public void Dispose()
    {
        _archive.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
