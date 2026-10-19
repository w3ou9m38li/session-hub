namespace ClaudeLauncher.Core.Display;

/// <summary>
/// Shorter forms of a working-directory path for a column too narrow to hold it.
///
/// <para>The end of a path is what tells two working directories apart — the folder name —
/// so shortening keeps the tail and elides the head: <c>\\wsl.localhost\ubuntu\home\abc\proj</c>
/// becomes <c>…\abc\proj</c>, never <c>\\wsl.localhost\ubu…</c>. Which form fits is decided by
/// whoever measures the text; this only lists the forms, longest first.</para>
/// </summary>
public static class PathDisplay
{
    public const string Ellipsis = "…";

    /// <summary>
    /// The path itself, then ever shorter tails, each prefixed with an ellipsis. The last
    /// entry is the folder name alone. A path with nothing to shorten yields just itself.
    /// </summary>
    public static IReadOnlyList<string> Candidates(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return [];

        var trimmed = path.Length > 3 ? path.TrimEnd('\\', '/') : path;
        if (trimmed.Length == 0) trimmed = path;

        var forms = new List<string> { trimmed };

        // Separator positions from the right. Each one marks where a shorter tail starts.
        for (var i = trimmed.Length - 1; i > 0; i--)
        {
            if (trimmed[i] is not ('\\' or '/')) continue;
            // A cut inside the UNC prefix or right after a drive letter saves nothing useful.
            var tail = trimmed[i..];
            if (tail.Length <= 1) continue;
            forms.Add(Ellipsis + tail);
        }

        // Longest first, with the full path always the first choice.
        var ordered = forms.Skip(1).Distinct(StringComparer.Ordinal)
                           .OrderByDescending(f => f.Length).ToList();
        ordered.Insert(0, trimmed);
        return ordered;
    }

    /// <summary>The last segment of a path — what a folder is called. Null for an empty path.</summary>
    public static string? FolderName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var trimmed = path.TrimEnd('\\', '/', ' ');
        if (trimmed.Length == 0) return path;
        var cut = trimmed.LastIndexOfAny(['\\', '/']);
        var tail = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
        return tail.Length == 0 ? trimmed : tail;
    }
}
