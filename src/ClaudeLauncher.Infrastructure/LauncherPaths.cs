using ClaudeLauncher.Infrastructure.Safety;

namespace ClaudeLauncher.Infrastructure;

/// <summary>
/// Everything this tool writes lives under one root. Claude's own directories are read-only
/// to us, and nothing is ever written outside this root.
///
/// <para>The root is a folder of this tool's own on the data drive —
/// <c>D:\ClaudeLauncherData</c> — so a catalog that grows with the conversation history does
/// not sit on the system drive. When that drive is missing or not writable (another machine,
/// a drive that is not ready) the old per-user location is used instead, and an existing
/// folder there is migrated on first run by <c>DataRootMigrator</c>.</para>
/// </summary>
public static class LauncherPaths
{
    public const string ProductFolder = "ClaudeSessionLauncher";

    /// <summary>Overrides the data root entirely. Checked against <see cref="PathPolicy"/> first.</summary>
    public const string DataRootVariable = "CLAUDE_LAUNCHER_DATA_DIR";

    /// <summary>The tool's own folder on the data drive. Deliberately not inside the source tree.</summary>
    public const string PreferredDataRoot = @"D:\ClaudeLauncherData";

    // Resolved once: the probe touches the disk, and the answer must not change mid-run.
    private static readonly Lazy<string> ResolvedRoot = new(() => _givenRoot ?? Resolve(
        Environment.GetEnvironmentVariable(DataRootVariable),
        PreferredDataRoot,
        LegacyDataRoot,
        CanHostData));

    // Set at start, before anything is opened, only when a move into the resolved root stopped short.
    private static string? _oldRootKept;

    // Set at start by a child process, to the root its parent resolved (UseDataRoot).
    private static string? _givenRoot;

    public static string DataRoot => _oldRootKept ?? ResolvedRoot.Value;

    /// <summary>
    /// Runs this process on the data root its parent resolved, instead of resolving one: a child
    /// the window starts must work in the folder the window works in — the old one, while a move
    /// is unfinished — whatever the variable or the drives say by now. Nothing is probed or
    /// written, and <see cref="DataRootFromVariable"/> stays false. Called once, before anything
    /// reads <see cref="DataRoot"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Not a folder that may hold this tool's data, or not there.</exception>
    /// <exception cref="InvalidOperationException">The data root was already read.</exception>
    public static void UseDataRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!PathPolicy.MayBeDataRoot(root, out var reason))
            throw new ArgumentException($"不能用作数据目录：{reason}", nameof(root));
        if (!Directory.Exists(root))
            throw new ArgumentException($"数据目录不存在：{root}", nameof(root));
        if (ResolvedRoot.IsValueCreated)
            throw new InvalidOperationException("数据目录已经定下，不能再换。");
        _givenRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    /// <summary>Whether this process runs on a root handed to it (<see cref="UseDataRoot"/>) rather than one it resolved.</summary>
    public static bool DataRootGiven => _givenRoot is not null;

    /// <summary>
    /// Keeps this run on the old folder: a move into the resolved root stopped short, and until the
    /// next start takes it up again the old folder is the only whole copy. A run on the new root
    /// would write there what that next attempt has to copy over.
    /// </summary>
    internal static void KeepOldRoot(string oldRoot) => _oldRootKept = oldRoot;

    /// <summary>
    /// Whether <see cref="DataRootVariable"/> named the data root actually in use. It can be set
    /// and still be ignored — a path the policy refuses, or one that cannot be written — and
    /// then this copy quietly shares the everyday catalog, which a window must not hide.
    /// </summary>
    public static bool DataRootFromVariable =>
        !DataRootGiven && ConfiguredRootInUse(Environment.GetEnvironmentVariable(DataRootVariable), DataRoot);

    public static bool ConfiguredRootInUse(string? configured, string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(configured)) return false;
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured)),
                Path.TrimEndingDirectorySeparator(dataRoot), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Where every version before 0.3.1 kept its data, and the fallback today.</summary>
    public static string LegacyDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolder);

    /// <summary>
    /// Picks the first candidate that is a legal data root and can actually be written to.
    /// Exposed for tests: the probe is injected so the decision can be exercised without
    /// depending on which drives the machine running the tests happens to have.
    /// </summary>
    public static string Resolve(
        string? configured, string preferred, string fallback, Func<string, bool> canHostData)
    {
        ArgumentNullException.ThrowIfNull(canHostData);

        foreach (var candidate in new[] { configured, preferred })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            // A root inside Claude's data or at a drive root is refused even when the user
            // asked for it by environment variable — every later write would inherit it.
            if (!PathPolicy.MayBeDataRoot(candidate, out _)) continue;
            if (!canHostData(candidate)) continue;
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        }
        return fallback;
    }

    /// <summary>
    /// How the name of the file written and deleted again to prove a folder writable begins. It
    /// may be met there for a moment, or left behind by a process that ended in between.
    /// </summary>
    internal const string WriteProbeFileName = ".write-probe";

    /// <summary>
    /// This process's own probe. Processes starting together — agents each starting a server of
    /// this tool — would otherwise meet on one file, and the one that lost would quietly fall
    /// back to the old folder.
    /// </summary>
    internal static string WriteProbeName =>
        WriteProbeFileName + "-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Creating the folder and a probe file is the only honest test of "writable".</summary>
    internal static bool CanHostData(string candidate)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(candidate));
            if (string.IsNullOrEmpty(root) || !new DriveInfo(root).IsReady) return false;

            Directory.CreateDirectory(candidate);
            var probe = Path.Combine(candidate, WriteProbeName);
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            // Missing drive, no permission, a path the OS rejects: fall back rather than fail.
            return false;
        }
    }

    public static string CatalogDatabase => Path.Combine(DataRoot, "catalog.sqlite");

    /// <summary>The full-text index of what was said. Derived from the transcripts; safe to delete.</summary>
    public static string ContentIndexDatabase => Path.Combine(DataRoot, "search-index.sqlite");

    /// <summary>The semantic index: what was said, found by meaning. Derived from the transcripts as well; safe to delete.</summary>
    public static string SemanticIndexDatabase => Path.Combine(DataRoot, "semantic-index.sqlite");

    /// <summary>
    /// Models the user put here for local semantic search. Not derived: this tool never downloads
    /// one, so a model deleted from here is gone until the user brings it back.
    /// </summary>
    public static string ModelsDirectory => Path.Combine(DataRoot, "models");

    /// <summary>Where the embedding model is looked for unless the settings name another folder.</summary>
    public static string DefaultEmbeddingModelFolder => Path.Combine(ModelsDirectory, "embedding");

    /// <summary>
    /// Gemini's web history imported from Google Takeout. Not derived: the exports it came from
    /// may be gone, and Google lets old activity go — deleting it loses what was imported.
    /// </summary>
    public static string GeminiTakeoutStore => Path.Combine(DataRoot, "gemini-web");

    /// <summary>ChatGPT's conversations on the web, imported from its data export. Not derived, for the same reason.</summary>
    public static string ChatGptWebStore => Path.Combine(DataRoot, "chatgpt-web");

    /// <summary>Claude's conversations on the web, imported from its data export. Not derived either.</summary>
    public static string ClaudeWebStore => Path.Combine(DataRoot, "claude-web");

    /// <summary>
    /// This tool's own copy of every transcript it lists, kept so a conversation stays readable
    /// after its app deletes or cleans up the record. Not derived: deleting it loses whatever
    /// the apps have deleted since.
    /// </summary>
    public static string ArchiveDirectory => Path.Combine(DataRoot, "archive");

    /// <summary>What was read from each terminal transcript, kept between runs. Derived; safe to delete.</summary>
    public static string ProbeCacheFile => Path.Combine(DataRoot, "probe-cache.json");

    /// <summary>Token usage read from the transcripts. Derived as well; safe to delete.</summary>
    public static string UsageDatabase => Path.Combine(DataRoot, "usage.sqlite");
    public static string DiagnosticsDirectory => Path.Combine(DataRoot, "diagnostics");
    public static string EvidenceDirectory => Path.Combine(DataRoot, "evidence");

    /// <summary>
    /// Memory-candidate drafts. Deliberately not a memory folder: a draft becomes a fact only
    /// after a human reads it and the memory tools write it.
    /// </summary>
    public static string MemoryDraftDirectory => Path.Combine(DataRoot, "memory-drafts");

    /// <summary>
    /// The fixed location for exported session indexes and transcripts. Stable across runs so
    /// the user can bookmark it and re-export over the top rather than hunting for a new folder.
    /// </summary>
    public static string ExportDirectory => Path.Combine(DataRoot, "export");

    /// <summary>Window placement, column widths and view choices. Losing it costs a preference, never data.</summary>
    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");

    /// <summary>
    /// Where CLI history lives. Honours CLAUDE_CONFIG_DIR, which is how a user who moved
    /// their history off the system drive keeps working — the default is only a fallback.
    /// </summary>
    public static string ClaudeConfigDir =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(DiagnosticsDirectory);
        Directory.CreateDirectory(EvidenceDirectory);
        Directory.CreateDirectory(MemoryDraftDirectory);
        Directory.CreateDirectory(ExportDirectory);
    }
}
