using System.Text.RegularExpressions;

namespace ClaudeLauncher.Infrastructure.Backends;

/// <summary>
/// The directory name Claude Code derives from a working directory:
/// <c>G:\ABC中期</c> becomes <c>G--ABC--</c>, one dash per character that is not ASCII
/// alphanumeric. The rule is lossy, so a slug can be checked against a path but never
/// turned back into one.
///
/// <para>Verified against every transcript on this machine: the first working directory a
/// transcript records matches its own project directory under this rule in 133 of 139 cases,
/// and <i>some</i> recorded working directory matches in 136. The remaining three are one
/// folder reachable under two drive letters, where the app's own records also disagree with
/// the directory name — so a mismatch is a warning, not an error.</para>
/// </summary>
public static partial class ProjectSlug
{
    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NotAlphanumeric();

    public static string FromPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return NotAlphanumeric().Replace(path, "-");
    }

    public static bool Matches(string path, string slug) =>
        string.Equals(FromPath(path), slug, StringComparison.Ordinal);
}
