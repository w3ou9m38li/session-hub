using System.Text;
using ClaudeLauncher.Infrastructure.Backends;

namespace ClaudeLauncher.IntegrationTests;

public sealed class SidebarGroupTests : IDisposable
{
    private readonly string _profile;

    public SidebarGroupTests()
    {
        _profile = Path.Combine(Path.GetTempPath(), "launcher-groups", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_profile);
    }

    private void WriteConfig(string json) =>
        File.WriteAllText(Path.Combine(_profile, "claude_desktop_config.json"), json, new UTF8Encoding(false));

    private const string Valid = """
        {
          "preferences": {
            "epitaxyPrefs": {
              "dframe-group-scopes": {
                "acct/org": {
                  "groups": [
                    { "id": "cg-1", "name": "病级提取" },
                    { "id": "cg-2", "name": "wheat GWAS merge" },
                    { "id": "cg-3", "name": "空分组" }
                  ],
                  "assignments": {
                    "code:local_aaa": "cg-1",
                    "code:local_bbb": "cg-2"
                  }
                }
              }
            }
          }
        }
        """;

    [Fact]
    public void AssignmentsResolveToGroupNames()
    {
        WriteConfig(Valid);
        var groups = SidebarGroupReader.Read(_profile);

        Assert.Equal("病级提取", groups.NameFor("local_aaa"));
        Assert.Equal("wheat GWAS merge", groups.NameFor("local_bbb"));
    }

    [Fact]
    public void UnassignedSessionHasNoGroup()
    {
        WriteConfig(Valid);
        Assert.Null(SidebarGroupReader.Read(_profile).NameFor("local_never_assigned"));
    }

    [Fact]
    public void OnlyCodePrefixedAssignmentsAreUsed()
    {
        WriteConfig("""
            {"preferences":{"epitaxyPrefs":{"dframe-group-scopes":{"s":{
              "groups":[{"id":"cg-1","name":"G"}],
              "assignments":{"chat:conv_xyz":"cg-1","code:local_ok":"cg-1"}}}}}}
            """);
        var groups = SidebarGroupReader.Read(_profile);

        // A chat assignment must not be mistaken for a Desktop Code session id.
        Assert.Equal("G", groups.NameFor("local_ok"));
        Assert.Null(groups.NameFor("conv_xyz"));
        Assert.Single(groups.GroupIdBySessionId);
    }

    [Fact]
    public void MalformedConfigDegradesToNoGroupsNotACrash()
    {
        WriteConfig("{ this is not json");
        Assert.True(SidebarGroupReader.Read(_profile).IsEmpty);
    }

    [Fact]
    public void ShapeDriftAfterAnUpdateDegradesGracefully()
    {
        // A future Claude build renames or moves the preference: no labels, no exception.
        WriteConfig("""{"preferences":{"somethingElse":{"groups":[]}}}""");
        Assert.True(SidebarGroupReader.Read(_profile).IsEmpty);
    }

    [Fact]
    public void MissingConfigFileIsNotAnError()
    {
        Assert.True(SidebarGroupReader.Read(_profile).IsEmpty);
    }

    [Fact]
    public void GroupWithNoAssignmentIsKnownButLabelsNothing()
    {
        WriteConfig(Valid);
        var groups = SidebarGroupReader.Read(_profile);

        // "空分组" exists in the cache but labels no session — absence of a label is not
        // evidence the group was deleted.
        Assert.Contains("cg-3", groups.GroupNamesById.Keys);
        Assert.DoesNotContain("cg-3", groups.GroupIdBySessionId.Values);
    }

    [Fact]
    public void ReaderNeverWritesTheConfigFile()
    {
        WriteConfig(Valid);
        var path = Path.Combine(_profile, "claude_desktop_config.json");
        var before = File.GetLastWriteTimeUtc(path);
        var bytes = new FileInfo(path).Length;

        SidebarGroupReader.Read(_profile);

        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
        Assert.Equal(bytes, new FileInfo(path).Length);
    }

    public void Dispose()
    {
        try { Directory.Delete(_profile, recursive: true); } catch { /* temp dir */ }
    }
}
