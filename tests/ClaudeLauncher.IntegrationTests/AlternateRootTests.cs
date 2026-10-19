using ClaudeLauncher.Infrastructure.Discovery;

namespace ClaudeLauncher.IntegrationTests;

public sealed class AlternateRootTests : IDisposable
{
    private readonly string _base;

    public AlternateRootTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "launcher-roots", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_base);
    }

    private string MakeRoot(string name, params string[] sessionIds)
    {
        var root = Path.Combine(_base, name);
        var project = Path.Combine(root, "projects", "G--demo");
        Directory.CreateDirectory(project);
        foreach (var id in sessionIds)
            File.WriteAllText(Path.Combine(project, id + ".jsonl"), "{}\n");
        return root;
    }

    [Fact]
    public void TheActiveRootIsNeverReportedAsAnAlternate()
    {
        var active = MakeRoot("active", "a", "b", "c");
        Assert.DoesNotContain(AlternateRootProbe.Probe(active),
            r => r.Path.Contains(Path.Combine("active", "projects"), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProbeIsReadOnly()
    {
        var active = MakeRoot("active", "a", "b");
        var project = Path.Combine(active, "projects", "G--demo");
        var before = Directory.GetFiles(project).Length;

        AlternateRootProbe.Probe(active);

        Assert.Equal(before, Directory.GetFiles(project).Length);
    }

    [Fact]
    public void MissingRootIsNotInvented()
    {
        var nowhere = Path.Combine(_base, "does-not-exist");
        Assert.All(AlternateRootProbe.Probe(nowhere), r => Assert.True(Directory.Exists(r.Path)));
    }

    [Fact]
    public void EveryReportedRootCarriesACountAndAReason()
    {
        foreach (var root in AlternateRootProbe.Probe(Path.Combine(_base, "active")))
        {
            Assert.True(root.TopLevelTranscripts > 0);
            Assert.False(string.IsNullOrWhiteSpace(root.Reason));
            Assert.InRange(root.NotInActiveRoot, 0, root.TopLevelTranscripts);
        }
    }

    [Fact]
    public void ARootWhoseContentIsAlreadyIndexedIsRedundantNotMissing()
    {
        // This is the state after restoring a backup into the active root: the old copy
        // still exists but holds nothing new. Reporting its size as a gap would be wrong.
        var subsetOfActive = new AlternateRoot(@"X:\old\projects", 133, 0, "旧副本");

        Assert.True(subsetOfActive.IsRedundantCopy);
    }

    [Fact]
    public void ARootHoldingUnseenSessionsIsARealGap()
    {
        var partlyNew = new AlternateRoot(@"X:\other\projects", 133, 97, "另一账户");

        Assert.False(partlyNew.IsRedundantCopy);
        // The gap is the shortfall, not the root's total size.
        Assert.Equal(97, partlyNew.NotInActiveRoot);
        Assert.NotEqual(partlyNew.TopLevelTranscripts, partlyNew.NotInActiveRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* temp dir */ }
    }
}
