using System.Text;
using ClaudeLauncher.Infrastructure.Settings;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// A preference file is the least important thing this tool writes, so the tests are about it
/// never costing more than a preference: no crash on a bad file, no window placed nowhere.
/// </summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public SettingsStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "launcher-settings", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");
    }

    [Fact]
    public void WhatIsSavedIsWhatIsLoaded()
    {
        var saved = new LauncherSettings
        {
            MainWindow = new WindowPlacement(100, 80, 1400, 900, Maximized: true),
            ColumnWidths = new() { ["group"] = 120, ["project"] = 180 },
            AppFilter = "Codex",
            CoverageExpanded = true,
            TranscriptDialogueOnly = true,
        };

        Assert.True(SettingsStore.Save(_path, saved));
        var loaded = SettingsStore.Load(_path);

        Assert.Equal(saved.MainWindow, loaded.MainWindow);
        Assert.Equal(180, loaded.ColumnWidths["project"]);
        Assert.Equal("Codex", loaded.AppFilter);
        Assert.True(loaded.CoverageExpanded);
        Assert.True(loaded.TranscriptDialogueOnly);
        Assert.False(File.Exists(_path + ".tmp"));
    }

    [Theory]
    [InlineData("Claude", "Claude")]
    [InlineData("Codex", "Codex")]
    [InlineData("Gemini", "Gemini")]
    [InlineData("Copilot", null)]
    public void TheUsageWindowKeepsTheAppItCounts(string saved, string? loaded)
    {
        Assert.True(SettingsStore.Save(_path, new LauncherSettings { UsageApp = saved }));
        Assert.Equal(loaded, SettingsStore.Load(_path).UsageApp);
    }

    [Theory]
    [InlineData("PerCall", "PerCall")]
    [InlineData("Rewrites", "Rewrites")]
    [InlineData("Total", null)]
    [InlineData("rewrites", null)]
    [InlineData(null, null)]
    public void TheUsageWindowKeepsHowItRanks(string? saved, string? loaded)
    {
        Assert.True(SettingsStore.Save(_path, new LauncherSettings { UsageRank = saved }));
        Assert.Equal(loaded, SettingsStore.Load(_path).UsageRank);
    }

    [Theory]
    [InlineData(@"  D:\Models\bge-small-zh  ", @"D:\Models\bge-small-zh")]
    [InlineData(@"D:\Models\bge-small-zh\", @"D:\Models\bge-small-zh")]
    [InlineData(@"\\nas\models\e5", @"\\nas\models\e5")]
    [InlineData(@"models\embedding", null)]
    [InlineData(@"\models", null)]
    [InlineData("   ", null)]
    [InlineData("D:\\a|b", null)]
    [InlineData(null, null)]
    public void SemanticSearchIsOffByDefaultAndItsModelFolderIsAFullPathOrNone(string? saved, string? loaded)
    {
        Assert.False(new LauncherSettings().SemanticSearch);
        Assert.True(SettingsStore.Save(_path, new LauncherSettings { SemanticSearch = true, SemanticModelFolder = saved }));
        var settings = SettingsStore.Load(_path);
        Assert.True(settings.SemanticSearch);
        Assert.Equal(loaded, settings.SemanticModelFolder);
    }

    [Fact]
    public void AMissingFileReadsAsDefaults()
    {
        var loaded = SettingsStore.Load(Path.Combine(_dir, "nope.json"));
        Assert.Null(loaded.MainWindow);
        Assert.Empty(loaded.ColumnWidths);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    public void ADamagedFileReadsAsDefaults(string content)
    {
        File.WriteAllText(_path, content, Encoding.UTF8);
        var loaded = SettingsStore.Load(_path);
        Assert.Null(loaded.MainWindow);
    }

    [Fact]
    public void ImplausibleValuesAreDroppedRatherThanApplied()
    {
        File.WriteAllText(_path, """
            {
              "MainWindow": { "Left": 0, "Top": 0, "Width": 0, "Height": -5, "Maximized": false },
              "ColumnWidths": { "group": 5, "project": 180, "": 90 }
            }
            """, Encoding.UTF8);

        var loaded = SettingsStore.Load(_path);

        Assert.Null(loaded.MainWindow);
        Assert.Equal(["project"], loaded.ColumnWidths.Keys);
    }

    [Fact]
    public void TheComparisonWindowKeepsItsSizeButNotAnImpossibleOne()
    {
        var size = new WindowPlacement(40, 30, 1300, 820, Maximized: false);
        Assert.True(SettingsStore.Save(_path, new LauncherSettings { CompareWindow = size }));
        Assert.Equal(size, SettingsStore.Load(_path).CompareWindow);

        File.WriteAllText(_path, """{ "CompareWindow": { "Left": 0, "Top": 0, "Width": 90000, "Height": 800, "Maximized": false } }""",
            Encoding.UTF8);
        Assert.Null(SettingsStore.Load(_path).CompareWindow);
    }

    [Theory]
    [InlineData("Title", "Title")]
    [InlineData("2", "Group")]          // a number that names a column is that column
    [InlineData("7", null)]             // one that names none must not reach the window
    [InlineData("Nonsense", null)]
    public void OnlyARealSortColumnIsKept(string saved, string? expected)
    {
        File.WriteAllText(_path, $$"""{ "SortField": "{{saved}}" }""", Encoding.UTF8);

        Assert.Equal(expected, SettingsStore.Load(_path).SortField);
    }

    [Fact]
    public void RecentSearchesAreKeptCleanAndGoWhenNotWanted()
    {
        SettingsStore.Save(_path, new LauncherSettings { RecentSearches = ["kasp", "病级"] });
        var loaded = SettingsStore.Load(_path);
        Assert.Equal(["kasp", "病级"], loaded.RecentSearches);
        Assert.True(loaded.RememberSearches);

        File.WriteAllText(_path, """{ "RecentSearches": [" kasp ", "KASP", "x", null, "病级"] }""", Encoding.UTF8);
        Assert.Equal(["kasp", "病级"], SettingsStore.Load(_path).RecentSearches);

        File.WriteAllText(_path, """{ "RecentSearches": null }""", Encoding.UTF8);
        Assert.Empty(SettingsStore.Load(_path).RecentSearches);

        // Turned off: none are kept, whatever the file still says.
        File.WriteAllText(_path, """{ "RememberSearches": false, "RecentSearches": ["kasp"] }""", Encoding.UTF8);
        Assert.Empty(SettingsStore.Load(_path).RecentSearches);
    }

    [Fact]
    public void ExportsHandledAreKeptCleanFewAndNewestFirst()
    {
        var at = new DateTime(2026, 10, 15, 1, 2, 3, DateTimeKind.Utc).AddTicks(4567);
        HandledExport Zip(int i, string outcome = HandledExport.Imported, string kind = "ChatGpt") =>
            new($@"D:\Downloads\export-{i}.zip", 1000 + i, at.AddMinutes(i), kind, outcome);

        var list = new List<HandledExport>();
        for (var i = 0; i < 40; i++) list = HandledExport.Add(list, Zip(i));
        Assert.Equal(HandledExport.Capacity, list.Count);
        Assert.Equal(Zip(39), list[0]);                                   // newest first, the oldest let go

        // Said again about the same file: the newer word, once, at the top.
        list = HandledExport.Add(list, Zip(20, HandledExport.Dismissed));
        Assert.Equal(Zip(20, HandledExport.Dismissed), list[0]);
        Assert.Single(list, h => h.Path.EndsWith("export-20.zip", StringComparison.Ordinal));

        var damaged = new List<HandledExport>(list)
        {
            new("export-x.zip", 1, at, "ChatGpt", HandledExport.Imported),             // not a full path
            new(@"D:\Downloads\y.zip", -1, at, "Claude", HandledExport.Imported),
            new(@"D:\Downloads\y.zip", 1, at, "Copilot", HandledExport.Imported),
            new(@"D:\Downloads\y.zip", 1, at, "1", HandledExport.Imported),             // a number names a kind too
            new(@"D:\Downloads\y.zip", 1, at, "GeminiTakeout", "maybe"),
            null!,
        };
        Assert.True(SettingsStore.Save(_path, new LauncherSettings { HandledExports = damaged }));
        var loaded = SettingsStore.Load(_path).HandledExports;
        Assert.Equal(list, loaded);                                       // times come back to the tick, in UTC

        var found = new Infrastructure.Backends.FoundExport(@"d:\downloads\EXPORT-20.zip", at.AddMinutes(20), 1020);
        Assert.True(HandledExport.Contains(loaded, found));
        Assert.False(HandledExport.Contains(loaded, found with { Bytes = 1021 }));   // downloaded anew
        Assert.False(HandledExport.Contains(loaded, found with { SavedUtc = found.SavedUtc.AddSeconds(1) }));

        Assert.All(loaded, h => Assert.Equal(DateTimeKind.Utc, h.WriteUtc.Kind));

        // Edited by hand: a time without its zone is read as UTC, what no offer can use is dropped.
        File.WriteAllText(_path, """
            { "HandledExports": [
                { "Path": "D:\\Downloads\\a.zip", "Bytes": 5, "WriteUtc": "2026-10-15T01:02:03", "Kind": "Claude", "Outcome": "dismissed" },
                { "Path": "a.zip", "Bytes": 5, "WriteUtc": "2026-10-15T01:02:03Z", "Kind": "Claude", "Outcome": "dismissed" } ] }
            """, Encoding.UTF8);
        var edited = Assert.Single(SettingsStore.Load(_path).HandledExports);
        Assert.Equal(new DateTime(2026, 10, 15, 1, 2, 3, DateTimeKind.Utc), edited.WriteUtc);
        Assert.Equal(DateTimeKind.Utc, edited.WriteUtc.Kind);

        File.WriteAllText(_path, """{ "HandledExports": null }""", Encoding.UTF8);
        Assert.Empty(SettingsStore.Load(_path).HandledExports);
    }

    [Fact]
    public void AFailedWriteReportsFalseAndLeavesTheOldFile()
    {
        SettingsStore.Save(_path, new LauncherSettings { AppFilter = "Claude" });

        // A directory where the file should go makes the move fail.
        var blocked = Path.Combine(_dir, "blocked.json");
        Directory.CreateDirectory(blocked);
        Assert.False(SettingsStore.Save(blocked, new LauncherSettings()));

        Assert.Equal("Claude", SettingsStore.Load(_path).AppFilter);
        Assert.False(File.Exists(blocked + ".tmp"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }
}
