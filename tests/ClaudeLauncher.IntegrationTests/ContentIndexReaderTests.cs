using System.Security.Cryptography;
using System.Text;
using ClaudeLauncher.Infrastructure.Search;
using Microsoft.Data.Sqlite;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// The full-text index read from a process beside the window: it sees what the window wrote,
/// finds what the window finds, and never changes the file — not even one it cannot use.
/// </summary>
public sealed class ContentIndexReaderTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public ContentIndexReaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "launcher-index-reader", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "search-index.sqlite");
    }

    private static string User(string text, string at = "2026-10-01T08:00:00Z") =>
        "{\"type\":\"user\",\"timestamp\":\"" + at + "\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}}";

    private static string Assistant(string text) =>
        "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}}";

    private string Transcript(string name, params string[] lines)
    {
        var path = Path.Combine(_dir, name + ".jsonl");
        File.WriteAllText(path, string.Join('\n', lines) + '\n', new UTF8Encoding(false));
        return path;
    }

    [Fact]
    public void TheReaderSeesWhatTheWindowWrote()
    {
        using var index = new ContentIndex(_path);
        var a = Transcript("a", User("KASP 引物怎么设计"), Assistant("先确定候选基因的位置"), User("病级提取也要"));
        index.Update([new("cli-code|acc|org|a", a), new("cli-code|acc|org|b", Transcript("b", User("别的会话")))]);

        using var reader = ContentIndexReader.Open(_path);
        Assert.NotNull(reader);
        Assert.Equal(2, reader.Count());

        var sources = reader.Sources();
        Assert.Equal(["cli-code|acc|org|a", "cli-code|acc|org|b"], sources.Select(s => s.Conversation));
        Assert.Equal(3, sources[0].Messages);
        Assert.Equal(new FileInfo(a).Length, sources[0].Length);

        var messages = reader.Messages("cli-code|acc|org|a");
        Assert.Equal([0, 1, 2], messages.Select(m => m.Ordinal));
        Assert.Equal(["user", "assistant", "user"], messages.Select(m => m.Role));
        Assert.Equal("先确定候选基因的位置", messages[1].Text);
        Assert.NotNull(messages[0].Stamp);
        Assert.Null(messages[1].Stamp);
        Assert.Equal(["KASP 引物怎么设计", "先确定候选基因的位置"], reader.Messages("cli-code|acc|org|a", 2).Select(m => m.Text));
        Assert.Empty(reader.Messages("cli-code|acc|org|nowhere"));

        // Found exactly as the window finds it: through the trigram index, by a scan, by the user only.
        foreach (var (term, userOnly) in new[] { ("候选基因", false), ("kasp", false), ("病级", false), ("候选基因", true), ("病级", true) })
            Assert.Equal(index.Hits("cli-code|acc|org|a", term, 3, userOnly), reader.Hits("cli-code|acc|org|a", term, 3, userOnly));
        Assert.Single(reader.Hits("cli-code|acc|org|a", "病级"));
        Assert.Empty(reader.Hits("cli-code|acc|org|a", "候选基因", userOnly: true));
        Assert.Empty(reader.Hits("cli-code|acc|org|a", "x"));
    }

    [Fact]
    public void TheFingerprintChangesWithTheIndexOnly()
    {
        using var index = new ContentIndex(_path);
        var path = Transcript("a", User("开头"));
        index.Update([new("k|a|o|a", path)]);
        using var reader = ContentIndexReader.Open(_path)!;
        var first = reader.Fingerprint();

        index.Update([new("k|a|o|a", path)]);                          // nothing new
        Assert.Equal(first, reader.Fingerprint());

        File.AppendAllText(path, User("后来的话") + "\n", new UTF8Encoding(false));
        index.Update([new("k|a|o|a", path)]);
        var grown = reader.Fingerprint();
        Assert.NotEqual(first, grown);

        index.Update([new("k|a|o|a", path), new("k|a|o|b", Transcript("b", User("新会话")))]);
        Assert.NotEqual(grown, reader.Fingerprint());
        Assert.Equal(2, reader.Count());
    }

    [Fact]
    public void NoIndexOrAnotherLayoutIsNotOpenedAndLeftAsItIs()
    {
        Assert.Null(ContentIndexReader.Open(_path));
        Assert.False(File.Exists(_path));

        // Written by another version: neither read nor dropped.
        using (var other = new SqliteConnection($"Data Source={_path};Pooling=False"))
        {
            other.Open();
            using var cmd = other.CreateCommand();
            cmd.CommandText = "CREATE TABLE sources(id INTEGER PRIMARY KEY, conversation TEXT); PRAGMA user_version=2;";
            cmd.ExecuteNonQuery();
        }
        var before = Hash(_path);
        Assert.Null(ContentIndexReader.Open(_path));
        Assert.Equal(before, Hash(_path));

        var junk = Path.Combine(_dir, "junk.sqlite");
        File.WriteAllText(junk, "not a database at all, just some text that is long enough to be read");
        Assert.Null(ContentIndexReader.Open(junk));
        Assert.Equal("not a database at all, just some text that is long enough to be read", File.ReadAllText(junk));
    }

    [Fact]
    public void ReadingNeverChangesTheFile()
    {
        using (var index = new ContentIndex(_path))
            index.Update([new("k|a|o|a", Transcript("a", User("KASP 引物"), Assistant("好的")))]);
        var before = Hash(_path);

        using (var reader = ContentIndexReader.Open(_path)!)
        {
            reader.Count();
            reader.Fingerprint();
            reader.Sources();
            reader.Messages("k|a|o|a");
            reader.Hits("k|a|o|a", "引物");
            reader.Hits("k|a|o|a", "kasp");
        }
        Assert.Equal(before, Hash(_path));

        // And the window goes on writing beside an open reader.
        using var again = new ContentIndex(_path);
        using var open = ContentIndexReader.Open(_path)!;
        again.Update([new("k|a|o|a", Transcript("a", User("KASP 引物"), Assistant("好的"), User("再来一句")))]);
        Assert.Equal(3, open.Messages("k|a|o|a").Count);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
