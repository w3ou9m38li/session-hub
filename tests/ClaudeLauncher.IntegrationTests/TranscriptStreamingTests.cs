using System.Text;
using ClaudeLauncher.Infrastructure.Backends;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// Reading page after page must continue where the last page stopped — correctly, and
/// without starting over from the top of a file that may be hundreds of megabytes.
/// </summary>
public sealed class TranscriptStreamingTests : IDisposable
{
    private readonly string _dir;

    public TranscriptStreamingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "launcher-stream", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    private static string User(string text) =>
        "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}}";

    private static string Assistant(string text) =>
        "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}}";

    private static string ToolResult(string payload) =>
        "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":\"" + payload + "\"}]}}";

    private string Write(IEnumerable<string> lines, bool trailingNewline = true)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("n") + ".jsonl");
        File.WriteAllText(path, string.Join('\n', lines) + (trailingNewline ? "\n" : ""), new UTF8Encoding(false));
        return path;
    }

    [Fact]
    public void SequentialPagesCoverTheFileExactlyOnce()
    {
        var path = Write(Enumerable.Range(0, 1000).Select(i => User("消息" + i)));
        using var reader = new TranscriptReader(path);

        var seen = new List<string>();
        var offset = 0;
        while (true)
        {
            var page = reader.Read(offset, 64);
            seen.AddRange(page.Entries.Select(e => e.Text));
            offset = page.NextLineOffset;
            if (page.ReachedEnd) break;
        }

        Assert.Equal(1000, seen.Count);
        Assert.Equal(Enumerable.Range(0, 1000).Select(i => "消息" + i), seen);
    }

    [Fact]
    public void GoingBackToAnEarlierPageStillWorks()
    {
        var path = Write(Enumerable.Range(0, 30).Select(i => User("消息" + i)));
        using var reader = new TranscriptReader(path);

        var first = reader.Read(0, 10);
        reader.Read(first.NextLineOffset, 10);
        var again = reader.Read(0, 10);        // not sequential: must start over

        Assert.Equal(first.Entries.Select(e => e.Text), again.Entries.Select(e => e.Text));
    }

    [Fact]
    public void LinesAppendedAfterTheEndAreReadOnTheNextPage()
    {
        var path = Write([User("一"), User("二")]);
        using var reader = new TranscriptReader(path);
        var page = reader.Read(0, 100);
        Assert.True(page.ReachedEnd);

        // The app keeps writing to a conversation that is still going on.
        File.AppendAllText(path, User("三") + "\n", new UTF8Encoding(false));

        var more = reader.Read(page.NextLineOffset, 100);
        Assert.Equal(["三"], more.Entries.Select(e => e.Text));
    }

    [Fact]
    public void AnUnfinishedLastLineIsNotLostWhileItIsBeingWritten()
    {
        var complete = User("写完的");
        var half = User("写到一半的消息");
        var path = Path.Combine(_dir, "live.jsonl");
        File.WriteAllText(path, complete + "\n" + half[..(half.Length / 2)], new UTF8Encoding(false));

        using var reader = new TranscriptReader(path);
        var page = reader.Read(0, 100);
        Assert.Equal(["写完的"], page.Entries.Select(e => e.Text));

        // The writer finishes the line; the next page must contain it whole.
        File.AppendAllText(path, half[(half.Length / 2)..] + "\n", new UTF8Encoding(false));
        var next = reader.Read(page.NextLineOffset, 100);

        Assert.Equal(["写到一半的消息"], next.Entries.Select(e => e.Text));
    }

    [Fact]
    public void ReadingOnPastAnUnfinishedLineStartsAtThatLineNotAtTheTop()
    {
        // A long conversation its app is still writing: once the line it was in the middle of is
        // written, reading on reads that line, not the whole file once more to count up to it.
        var half = User("写到一半的消息");
        var path = Path.Combine(_dir, "live-long.jsonl");
        File.WriteAllText(path, string.Join('\n', Enumerable.Range(0, 2000).Select(i => User("消息" + i)))
                                + "\n" + half[..(half.Length / 2)], new UTF8Encoding(false));
        using var reader = new TranscriptReader(path);
        var offset = 0;
        TranscriptPage page;
        do
        {
            page = reader.Read(offset, 500);
            offset = page.NextLineOffset;
        } while (!page.ReachedEnd);
        Assert.Equal(2000, offset);

        File.AppendAllText(path, half[(half.Length / 2)..] + "\n" + User("又一条") + "\n", new UTF8Encoding(false));
        var next = reader.Read(offset, 100);

        Assert.Equal(["写到一半的消息", "又一条"], next.Entries.Select(e => e.Text));
        Assert.InRange(next.BytesScanned, 1, 1024);
    }

    [Fact]
    public void AnUnfinishedLineCutInsideACharacterIsReadWholeOnceWritten()
    {
        // The writer's last piece can end part way through a character of three bytes.
        var line = Encoding.UTF8.GetBytes(User("写到一半的消息") + "\n");
        var cut = Array.FindIndex(line, b => b >= 0x80) + 1;
        var path = Path.Combine(_dir, "live-cut.jsonl");
        File.WriteAllBytes(path, [.. Encoding.UTF8.GetBytes(User("写完的") + "\n"), .. line[..cut]]);
        using var reader = new TranscriptReader(path);
        var page = reader.Read(0, 100);
        Assert.Equal(["写完的"], page.Entries.Select(e => e.Text));

        using (var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            file.Write(line, cut, line.Length - cut);
        var next = reader.Read(page.NextLineOffset, 100);

        Assert.Equal(["写到一半的消息"], next.Entries.Select(e => e.Text));
        Assert.Equal(2, next.NextLineOffset);       // and counted once, from its own beginning
    }

    [Fact]
    public void AReaderAtTheEndNoticesTheFileWrittenOn()
    {
        var path = Write([User("一"), User("二")]);
        using var reader = new TranscriptReader(path);
        Assert.False(reader.ChangedSinceEnd);       // not read to the end yet
        var page = reader.Read(0, 100);
        Assert.True(page.ReachedEnd);
        Assert.False(reader.ChangedSinceEnd);

        // A reader left open on a conversation that goes on: 「最新」 has to know to read on.
        File.AppendAllText(path, User("三") + "\n", new UTF8Encoding(false));
        Assert.True(reader.ChangedSinceEnd);
        var more = reader.Read(page.NextLineOffset, 100);
        Assert.Equal(["三"], more.Entries.Select(e => e.Text));
        Assert.False(reader.ChangedSinceEnd);
    }

    [Fact]
    public void AFileMovedAwayWhileOpenIsReadOnThroughTheHandleItHas()
    {
        // Codex moves an archived task's rollout to archived_sessions; Claude Code's cleanup
        // deletes old transcripts. What is not read yet is still there to read, not an end.
        var moved = Write(Enumerable.Range(0, 30).Select(i => User("消息" + i)));
        var deleted = Write(Enumerable.Range(0, 30).Select(i => User("消息" + i)));
        using var movedReader = new TranscriptReader(moved);
        using var deletedReader = new TranscriptReader(deleted);
        var first = movedReader.Read(0, 10);
        deletedReader.Read(0, 10);
        Assert.False(first.ReachedEnd);

        File.Move(moved, moved + ".archived");
        File.Delete(deleted);

        foreach (var reader in new[] { movedReader, deletedReader })
        {
            Assert.False(reader.Exists);
            var rest = reader.Read(first.NextLineOffset, 100);
            Assert.Equal(Enumerable.Range(10, 20).Select(i => "消息" + i), rest.Entries.Select(e => e.Text));
            Assert.True(rest.ReachedEnd);
        }
    }

    [Fact]
    public void AFileGoneWithNothingOpenToReadOnWithIsNotItsEnd()
    {
        var complete = User("写完的");
        var half = User("写到一半的消息");
        var path = Path.Combine(_dir, "gone.jsonl");
        File.WriteAllText(path, complete + "\n" + half[..(half.Length / 2)], new UTF8Encoding(false));
        using var reader = new TranscriptReader(path);
        var page = reader.Read(0, 100);     // stops short of the unfinished line and lets go of the file

        File.Delete(path);

        // Part of it read and nothing open to read on with: an error the viewer can say, not "已到结尾".
        Assert.Throws<FileNotFoundException>(() => reader.Read(page.NextLineOffset, 100));
    }

    [Fact]
    public void AnOversizeLineBetweenPagesKeepsTheCountExact()
    {
        // A skipped line still counts as a line; otherwise the next page would start one off.
        // Tool output is what gets skipped: a message this long is kept (see TranscriptReaderTests).
        var huge = "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":\""
                   + new string('x', 5 * 1024 * 1024) + "\"}]}}";
        var path = Write([User("一"), huge, User("二"), User("三")]);
        using var reader = new TranscriptReader(path);

        var seen = new List<string>();
        var offset = 0;
        for (var guard = 0; guard < 10; guard++)
        {
            var page = reader.Read(offset, 1);
            seen.AddRange(page.Entries.Select(e => e.Text));
            offset = page.NextLineOffset;
            if (page.ReachedEnd) break;
        }

        Assert.Equal(["一", "二", "三"], seen);
        Assert.Equal(4, offset);
    }

    [Fact]
    public void AFinalLineWithoutNewlineIsStillRead()
    {
        var page = new TranscriptReader(Write([User("最后一行")], trailingNewline: false)).Read();
        Assert.Equal(["最后一行"], page.Entries.Select(e => e.Text));
    }

    [Fact]
    public void ReadAllReturnsTheWholeConversationAndReportsProgress()
    {
        var lines = Enumerable.Range(0, 3000)
            .Select(i => i % 3 == 2 ? ToolResult("输出" + i) : i % 3 == 0 ? User("问" + i) : Assistant("答" + i));
        var path = Write(lines);
        var reports = new List<double>();

        var content = TranscriptExport.ReadAll(path, new SyncProgress(reports.Add));

        Assert.Equal(3000, content.Entries.Count);
        Assert.Equal(1000, content.ToolOutputsFolded);
        Assert.Equal(1.0, reports[^1]);
        Assert.All(reports, r => Assert.InRange(r, 0, 1));
    }

    [Fact]
    public void ADisposedReaderReleasesTheFile()
    {
        var path = Write([User("一")]);
        var reader = new TranscriptReader(path);
        reader.Read();
        reader.Dispose();

        // Nothing holds the file any more: it can be deleted outright.
        File.Delete(path);
        Assert.False(File.Exists(path));
        Assert.Throws<ObjectDisposedException>(() => reader.Read());
    }

    /// <summary>Progress&lt;T&gt; posts to a sync context; this one records on the calling thread.</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
