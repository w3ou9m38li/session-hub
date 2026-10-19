using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using ClaudeLauncher.Infrastructure;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// Tests that set <see cref="LauncherPaths.DataRootVariable"/>, which the whole process reads: alone.
/// A test running beside one — a Codex reader made without a snapshot folder — could resolve
/// the data root of every other test to the folder it names.
/// </summary>
[CollectionDefinition(nameof(DataRootVariable), DisableParallelization = true)]
public sealed class DataRootVariable;

/// <summary>
/// A child the window starts works on the data root the window hands it, never on one it
/// resolves itself. The data root is process-wide and resolved once, so each test runs on a copy
/// of its own of the library, loaded beside this one: what it sets never reaches another test,
/// and nothing here ever resolves this machine's real root.
/// </summary>
[Collection(nameof(DataRootVariable))]
public sealed class DataRootHandoverTests : IDisposable
{
    private readonly string _dir;

    public DataRootHandoverTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "launcher-handover", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    /// <summary>LauncherPaths and DataRootMigrator of a fresh copy of the library: nothing resolved, nothing prepared.</summary>
    private sealed class Fresh : IDisposable
    {
        private readonly AssemblyLoadContext _context = new("data-root-" + Guid.NewGuid().ToString("n"), isCollectible: true);
        private readonly Type _paths;
        private readonly Type _migrator;

        public Fresh()
        {
            var library = _context.LoadFromAssemblyPath(typeof(LauncherPaths).Assembly.Location);
            _paths = library.GetType(typeof(LauncherPaths).FullName!)!;
            _migrator = library.GetType(typeof(DataRootMigrator).FullName!)!;
        }

        public string DataRoot => Value(nameof(LauncherPaths.DataRoot));

        public string Value(string property) => (string)_paths.GetProperty(property)!.GetValue(null)!;
        public bool Given => (bool)_paths.GetProperty(nameof(LauncherPaths.DataRootGiven))!.GetValue(null)!;
        public bool FromVariable => (bool)_paths.GetProperty(nameof(LauncherPaths.DataRootFromVariable))!.GetValue(null)!;

        public void UseDataRoot(string root) => Call(_paths.GetMethod(nameof(LauncherPaths.UseDataRoot))!, root);

        /// <summary>What a child does first: the old folder is the test's own.</summary>
        public string Prepare(string legacyRoot)
        {
            var prepare = _migrator.GetMethod(nameof(DataRootMigrator.PrepareDataRoot),
                BindingFlags.Static | BindingFlags.NonPublic, [typeof(bool), typeof(string)])!;
            var report = Call(prepare, false, legacyRoot)!;
            return report.GetType().GetProperty(nameof(MigrationReport.Outcome))!.GetValue(report)!.ToString()!;
        }

        private static object? Call(MethodInfo method, params object?[] args)
        {
            try { return method.Invoke(null, args); }
            catch (TargetInvocationException ex) when (ex.InnerException is { } inner)
            {
                ExceptionDispatchInfo.Throw(inner);
                throw;
            }
        }

        public void Dispose() => _context.Unload();
    }

    [Fact]
    public void AGivenRootIsUsedAsItIsWithoutAProbe()
    {
        var root = Path.Combine(_dir, "root");
        Directory.CreateDirectory(root);
        // A file made and deleted again in a folder moves the folder's write time.
        var untouched = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(root, untouched);
        var before = Environment.GetEnvironmentVariable(LauncherPaths.DataRootVariable);
        Environment.SetEnvironmentVariable(LauncherPaths.DataRootVariable, root);
        try
        {
            using var fresh = new Fresh();
            fresh.UseDataRoot(root + Path.DirectorySeparatorChar);

            Assert.Equal(root, fresh.DataRoot);
            Assert.True(fresh.Given);
            Assert.False(fresh.FromVariable);       // handed down, even where the variable names it too
            Assert.Equal(untouched, Directory.GetLastWriteTimeUtc(root));      // no probe

            // Read once, it stays.
            Assert.Throws<InvalidOperationException>(() => fresh.UseDataRoot(_dir));
            Assert.Equal(root, fresh.DataRoot);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LauncherPaths.DataRootVariable, before);
        }
    }

    [Fact]
    public void AChildNeverJudgesTheMoveAgain()
    {
        // An old folder beside a new root that holds nothing yet: a run that judged for itself would
        // take the old one, while the window works in the new one.
        var legacy = Path.Combine(_dir, "old");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "catalog.sqlite"), "db");
        var root = Path.Combine(_dir, "new");
        Directory.CreateDirectory(root);

        using var fresh = new Fresh();
        fresh.UseDataRoot(root);
        Assert.Equal(nameof(MigrationOutcome.NothingToDo), fresh.Prepare(legacy));
        Assert.Equal(root, fresh.DataRoot);
        Assert.True(File.Exists(Path.Combine(legacy, "catalog.sqlite")));
    }

    [Fact]
    public void TheSemanticIndexAndTheModelsLiveUnderTheRoot()
    {
        using var fresh = new Fresh();
        fresh.UseDataRoot(_dir);
        Assert.Equal(Path.Combine(_dir, "semantic-index.sqlite"), fresh.Value(nameof(LauncherPaths.SemanticIndexDatabase)));
        Assert.Equal(Path.Combine(_dir, "models"), fresh.Value(nameof(LauncherPaths.ModelsDirectory)));
        Assert.Equal(Path.Combine(_dir, "models", "embedding"), fresh.Value(nameof(LauncherPaths.DefaultEmbeddingModelFolder)));
    }

    [Fact]
    public void ARootThatMayNotHoldDataOrIsNotThereIsRefused()
    {
        using var fresh = new Fresh();
        Assert.Throws<ArgumentException>(() => fresh.UseDataRoot(Path.Combine(_dir, "missing")));
        var claudes = Path.Combine(_dir, ".claude", "data");
        Directory.CreateDirectory(claudes);
        Assert.Throws<ArgumentException>(() => fresh.UseDataRoot(claudes));
        Assert.False(fresh.Given);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
