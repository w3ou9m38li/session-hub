namespace ClaudeLauncher.Infrastructure.Discovery;

/// <summary>
/// A CLI history root that exists on disk but is not the one being scanned.
/// </summary>
/// <param name="TopLevelTranscripts">Everything in that root.</param>
/// <param name="NotInActiveRoot">
/// How many of them the active root does not have. This — not the total — is the real gap.
/// Zero means the root is a redundant copy, which is very different from missing history.
/// </param>
public sealed record AlternateRoot(
    string Path,
    int TopLevelTranscripts,
    int NotInActiveRoot,
    string Reason)
{
    /// <summary>True when the root holds nothing the active root is missing.</summary>
    public bool IsRedundantCopy => NotInActiveRoot == 0;
}

/// <summary>
/// Looks for CLI history outside the active <c>CLAUDE_CONFIG_DIR</c>.
///
/// History can end up split across roots — a config-dir migration that left the old copy
/// behind, a second Windows account, a restored backup. Scanning only the active root and
/// saying nothing would present a partial list as if it were complete.
///
/// Counting files is not enough, though: after a restore the old root is usually a strict
/// subset of the active one, and reporting its size as "not indexed" would imply missing
/// conversations that are in fact already there. So the probe compares session ids and
/// reports the genuine shortfall.
///
/// Strictly read-only: it lists file names and never opens, moves or merges anything.
/// </summary>
public static class AlternateRootProbe
{
    public static IReadOnlyList<AlternateRoot> Probe(string activeConfigDir)
    {
        var activeProjects = Path.Combine(activeConfigDir, "projects");
        var activeIds = SessionIds(activeProjects);
        var activeKey = Normalize(activeProjects);
        var found = new List<AlternateRoot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (candidate, reason) in Candidates())
        {
            var projects = Path.Combine(candidate, "projects");
            var key = Normalize(projects);
            if (key == activeKey || !seen.Add(key)) continue;
            if (!Directory.Exists(projects)) continue;

            var ids = SessionIds(projects);
            if (ids.Count == 0) continue;

            var missing = ids.Count(id => !activeIds.Contains(id));
            found.Add(new AlternateRoot(projects, ids.Count, missing, reason));
        }

        return found;
    }

    /// <summary>Top-level transcript stems in a projects root. Nested files are sub-agents.</summary>
    private static HashSet<string> SessionIds(string projectsRoot)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(projectsRoot)) return ids;

        try
        {
            foreach (var project in Directory.EnumerateDirectories(projectsRoot))
                foreach (var file in Directory.EnumerateFiles(project, "*.jsonl", SearchOption.TopDirectoryOnly))
                    ids.Add(Path.GetFileNameWithoutExtension(file));
        }
        catch
        {
            // An unreadable root is not evidence of anything; return what we managed to see.
        }
        return ids;
    }

    private static IEnumerable<(string Path, string Reason)> Candidates()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // The default location. Populated while CLAUDE_CONFIG_DIR points elsewhere almost
        // always means a migration left the original copy in place.
        yield return (Path.Combine(profile, ".claude"), "默认位置（CLAUDE_CONFIG_DIR 已指向别处）");

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            var root = drive.RootDirectory.FullName;
            yield return (Path.Combine(root, "ClaudeData"), $"{drive.Name} 上的 ClaudeData");
            yield return (Path.Combine(root, ".claude"), $"{drive.Name} 根目录下的 .claude");
        }
    }

    private static string Normalize(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToLowerInvariant(); }
        catch { return path.ToLowerInvariant(); }
    }
}
