using ClaudeLauncher.Core.Contracts;
using ClaudeLauncher.Infrastructure.Backends;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>What is read from Claude's own record of a desktop session, file by file.</summary>
public sealed class DesktopRecordTests : IDisposable
{
    private readonly string _profile = Path.Combine(Path.GetTempPath(), "launcher-desktop", Guid.NewGuid().ToString("n"));
    private readonly string _store;

    public DesktopRecordTests()
    {
        _store = Directory.CreateDirectory(Path.Combine(_profile, "claude-code-sessions", "acc", "org")).FullName;
    }

    private void Write(string sessionId, string json) =>
        File.WriteAllText(Path.Combine(_store, sessionId + ".json"), json);

    [Fact]
    public void TheTranscriptsASessionRanOnBeforeAreRead()
    {
        Write("local_1", """
            { "sessionId": "local_1", "cliSessionId": "cli-new", "title": "项目完善",
              "priorCliSessionIds": ["cli-old", "", 7, "cli-older"] }
            """);
        Write("local_2", """{ "sessionId": "local_2", "cliSessionId": "cli-2", "title": "没续接过" }""");
        Write("local_3", """{ "sessionId": "local_3", "title": "形状不对", "priorCliSessionIds": "cli-x" }""");

        var rows = new DesktopCodeMetadataReader(_profile).Read().ToDictionary(r => r.Key.NativeSessionId);

        // Strings only: an empty entry or one of another type is not a transcript id.
        Assert.Equal(["cli-old", "cli-older"], rows["local_1"].PriorCliSessionIds);
        Assert.Empty(rows["local_2"].PriorCliSessionIds);
        Assert.Empty(rows["local_3"].PriorCliSessionIds);
        Assert.Equal(ConversationKey.DesktopCode, rows["local_1"].Key.Backend);
    }

    public void Dispose()
    {
        try { Directory.Delete(_profile, recursive: true); } catch { /* temp dir */ }
    }
}
