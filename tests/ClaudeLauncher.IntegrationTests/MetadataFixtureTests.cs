using System.Diagnostics;
using System.Text;
using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Backends;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// Synthetic fixtures only: generated UUIDs, generated titles, throwaway directories.
/// No user transcript, account id or body is ever copied into a test.
/// </summary>
public sealed class MetadataFixtureTests : IDisposable
{
    private readonly string _configDir;
    private readonly string _projects;

    public MetadataFixtureTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "launcher-fixtures", Guid.NewGuid().ToString("n"));
        _projects = Path.Combine(_configDir, "projects");
        Directory.CreateDirectory(_projects);
    }

    private string Project(string name)
    {
        var dir = Path.Combine(_projects, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteJsonl(string path, params string[] lines) =>
        File.WriteAllText(path, string.Join('\n', lines) + '\n', new UTF8Encoding(false));

    /// <summary>Builds one transcript line. Kept as plain concatenation so the JSON braces
    /// are unambiguous, which raw interpolated strings make awkward here.</summary>
    private static string UserLine(string text, string? cwd = null)
    {
        var cwdJson = cwd is null ? "null" : "\"" + cwd.Replace("\\", "\\\\") + "\"";
        return "{\"type\":\"user\",\"cwd\":" + cwdJson
             + ",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}}";
    }

    [Fact]
    public void SubagentJsonlIsNotARootConversation()
    {
        var project = Project("G--demo");
        WriteJsonl(Path.Combine(project, "aaaaaaaa-0000-0000-0000-000000000001.jsonl"),
            UserLine("顶层会话", @"G:\demo"));

        var nested = Path.Combine(project, "aaaaaaaa-0000-0000-0000-000000000001");
        Directory.CreateDirectory(nested);
        WriteJsonl(Path.Combine(nested, "subagent-1.jsonl"), UserLine("子代理"));

        var reader = new CliMetadataReader(_configDir);
        var rows = reader.Read("acc", "org").ToList();

        Assert.Single(rows);
        Assert.Equal(1, reader.CountNonRootTranscripts());
    }

    [Fact]
    public void HugeSingleLineStaysWithinByteBudget()
    {
        var project = Project("G--huge");
        var file = Path.Combine(project, "bbbbbbbb-0000-0000-0000-000000000002.jsonl");

        // One 40 MB line: a naive ReadLine would materialise all of it.
        var padding = new string('x', 40 * 1024 * 1024);
        File.WriteAllText(file, $"{{\"type\":\"other\",\"blob\":\"{padding}\"}}\n");

        var reader = new CliMetadataReader(_configDir, byteBudget: 256 * 1024);
        var sw = Stopwatch.StartNew();
        var rows = reader.Read("acc", "org").ToList();
        sw.Stop();

        Assert.Single(rows);
        // The row survives with a placeholder rather than being dropped.
        Assert.True(rows[0].TitleIsPlaceholder);
        Assert.Equal(SourceCoverage.Partial, rows[0].SourceCoverage);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"预算解析耗时 {sw.ElapsedMilliseconds} ms，疑似整行载入");
    }

    [Fact]
    public void MissingTitleStillAppearsInCatalog()
    {
        var project = Project("G--notitle");
        WriteJsonl(Path.Combine(project, "cccccccc-0000-0000-0000-000000000003.jsonl"),
            """{"type":"system","cwd":"G:\\notitle"}""");

        var rows = new CliMetadataReader(_configDir).Read("acc", "org").ToList();

        Assert.Single(rows);
        Assert.True(rows[0].TitleIsPlaceholder);
        Assert.Equal("cccccccc-0000-0000-0000-000000000003", rows[0].Title);
    }

    [Fact]
    public void BoilerplateFirstMessageIsNotUsedAsTitle()
    {
        var project = Project("G--boiler");
        WriteJsonl(Path.Combine(project, "dddddddd-0000-0000-0000-000000000004.jsonl"),
            UserLine("This session is being continued from a previous conversation", @"G:\boiler"),
            UserLine("真正的问题在这里", @"G:\boiler"));

        var rows = new CliMetadataReader(_configDir).Read("acc", "org").ToList();

        Assert.Single(rows);
        Assert.Equal("真正的问题在这里", rows[0].Title);
    }

    [Fact]
    public void CancellationStopsMetadataRead()
    {
        var project = Project("G--cancel");
        for (var i = 0; i < 50; i++)
            WriteJsonl(Path.Combine(project, $"eeeeeeee-0000-0000-0000-{i:D12}.jsonl"),
                UserLine($"会话 {i}", @"G:\cancel"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var reader = new CliMetadataReader(_configDir);
        Assert.Throws<OperationCanceledException>(() => reader.Read("acc", "org", cts.Token).ToList());
    }

    [Fact]
    public void MalformedLineIsIsolatedNotFatal()
    {
        var project = Project("G--broken");
        WriteJsonl(Path.Combine(project, "ffffffff-0000-0000-0000-000000000005.jsonl"),
            "{ this is not json",
            UserLine("坏行之后仍应解析", @"G:\broken"));

        var rows = new CliMetadataReader(_configDir).Read("acc", "org").ToList();

        Assert.Single(rows);
        Assert.Equal("坏行之后仍应解析", rows[0].Title);
    }

    [Fact]
    public void CliRowsAreMarkedAsHavingNoNativeArchive()
    {
        var project = Project("G--native");
        WriteJsonl(Path.Combine(project, "11111111-0000-0000-0000-000000000006.jsonl"),
            UserLine("任意标题", @"G:\native"));

        var rows = new CliMetadataReader(_configDir).Read("acc", "org").ToList();

        // CLI transcripts have no archive concept; claiming Unknown would imply one might exist.
        Assert.Equal(NativeArchiveState.Unsupported, rows[0].NativeArchiveState);
    }

    public void Dispose()
    {
        try { Directory.Delete(_configDir, recursive: true); } catch { /* temp dir */ }
    }
}
