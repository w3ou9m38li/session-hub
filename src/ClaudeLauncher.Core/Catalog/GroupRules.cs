using System.Text;

namespace ClaudeLauncher.Core.Catalog;

/// <summary>
/// Rules for this launcher's own conversation groups.
///
/// <para>A group here is purely this tool's own label. Claude has its own sidebar groups and
/// this launcher only ever reads those, so the two are kept as separate fields and combined
/// by <see cref="Effective"/> rather than merged on write — otherwise a Claude-side rename
/// would silently rewrite the user's own grouping, or the other way round.</para>
/// </summary>
public static class GroupRules
{
    /// <summary>
    /// Longest group name kept. A name is a column label, not a description; past this the
    /// list column becomes unreadable and the name stops working as a filter.
    /// </summary>
    public const int MaxLength = 40;

    /// <summary>
    /// The stored value meaning "the user said this conversation has no group", as distinct
    /// from <c>null</c>, which means "no preference — show Claude's group if it has one".
    /// </summary>
    public const string ExplicitlyUngrouped = "";

    /// <summary>
    /// Cleans a name typed by the user. Control characters are dropped and whitespace runs
    /// collapse to one space, so "组 A" and "组  A\t" cannot become two different groups that
    /// look identical in the list. Returns null when nothing usable is left.
    /// </summary>
    public static string? Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var sb = new StringBuilder(name.Length);
        var pendingSpace = false;
        foreach (var ch in name)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            // Control characters would render as boxes and could hide a difference between
            // two names, so they are removed rather than escaped.
            if (char.IsControl(ch)) continue;
            if (pendingSpace) sb.Append(' ');
            pendingSpace = false;
            sb.Append(ch);
        }

        if (sb.Length == 0) return null;
        return sb.Length > MaxLength ? sb.ToString(0, MaxLength) : sb.ToString();
    }

    /// <summary>
    /// The group to show and filter by: the user's own choice wins, Claude's own sidebar
    /// group is the fallback, and an explicit "no group" beats both. Null means ungrouped.
    /// </summary>
    public static string? Effective(string? localGroup, string? sidebarGroup) => localGroup switch
    {
        null => string.IsNullOrEmpty(sidebarGroup) ? null : sidebarGroup,
        ExplicitlyUngrouped => null,
        var own => own,
    };

    /// <summary>
    /// A group name derived from a working directory, for bulk grouping.
    ///
    /// <para>The last path segment is used: <c>G:\wheat GWAS 2</c> becomes "wheat GWAS 2" and
    /// <c>\\wsl.localhost\ubuntu\home\me\proj</c> becomes "proj". Splitting is done here rather
    /// than with <c>Path</c> so the result does not change with the host OS's separator —
    /// a transcript recorded on one machine keeps the same group name on another.</para>
    ///
    /// <para>A value that is not a path (a slugged folder name kept when a transcript never
    /// recorded a working directory) is used as it stands: it is still the best label we have.</para>
    /// </summary>
    public static string? FromProjectPath(string? projectRef)
    {
        if (string.IsNullOrWhiteSpace(projectRef)) return null;

        var trimmed = projectRef.TrimEnd('\\', '/', ' ');
        if (trimmed.Length == 0) return null;

        var cut = trimmed.LastIndexOfAny(['\\', '/']);
        var tail = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;

        // "D:" or "D:\" leaves nothing after the separator; the drive itself is the label.
        if (tail.Length == 0) tail = trimmed;

        return Normalize(tail);
    }
}
