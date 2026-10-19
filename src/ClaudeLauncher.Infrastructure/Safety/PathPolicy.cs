namespace ClaudeLauncher.Infrastructure.Safety;

/// <summary>
/// Guards every filesystem target this tool touches.
///
/// The launcher only ever reads Claude's data and only ever writes under its own data root,
/// so these checks exist to make that structural rather than a convention.
/// </summary>
public static class PathPolicy
{
    /// <summary>Roots this tool must never write to, delete from, or move.</summary>
    private static readonly string[] ForbiddenWriteRootNames =
    [
        ".claude",
        "WindowsApps",
        "claude-code-sessions",
    ];

    /// <summary>
    /// True only when <paramref name="candidate"/> resolves to a location inside
    /// <paramref name="allowedRoot"/>. Rejects traversal, and rejects a candidate that merely
    /// shares a name prefix with the root (".../data" vs ".../data-backup").
    /// </summary>
    public static bool IsWithin(string allowedRoot, string candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(allowedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(allowedRoot));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));

        if (string.Equals(root, full, StringComparison.OrdinalIgnoreCase)) return true;

        var prefix = root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a directory may hold this tool's own data.
    ///
    /// <para>Applied to the data root itself, which can be redirected by an environment
    /// variable: a root inside Claude's own data, inside the package install, or at a drive
    /// or profile root would turn every later write into a write somewhere it must not go,
    /// so it is refused once, here, rather than checked at each file.</para>
    /// </summary>
    public static bool MayBeDataRoot(string candidate, out string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);

        string full;
        try
        {
            full = Path.GetFullPath(candidate);
        }
        catch (Exception ex)
        {
            reason = "路径无法解析：" + ex.Message;
            return false;
        }

        var root = Path.GetPathRoot(full);
        if (string.Equals(Path.TrimEndingDirectorySeparator(full),
                          Path.TrimEndingDirectorySeparator(root ?? string.Empty),
                          StringComparison.OrdinalIgnoreCase))
        {
            reason = "拒绝驱动器根目录";
            return false;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.Equals(Path.TrimEndingDirectorySeparator(full),
                          Path.TrimEndingDirectorySeparator(userProfile),
                          StringComparison.OrdinalIgnoreCase))
        {
            reason = "拒绝用户主目录根";
            return false;
        }

        foreach (var segment in full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            foreach (var forbidden in ForbiddenWriteRootNames)
            {
                if (string.Equals(segment, forbidden, StringComparison.OrdinalIgnoreCase))
                {
                    reason = $"拒绝写入官方数据区域（{forbidden}）";
                    return false;
                }
            }
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Whether this tool may write to a path: everything <see cref="MayBeDataRoot"/> refuses,
    /// plus anything outside our own data root.
    /// </summary>
    public static bool MayWrite(string ownDataRoot, string candidate, out string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);

        if (!MayBeDataRoot(candidate, out reason)) return false;

        if (!IsWithin(ownDataRoot, candidate))
        {
            reason = "目标不在本工具数据根内";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// A reparse point (symlink / junction) could redirect a "safe" path anywhere, so any
    /// link encountered on a write target is refused rather than followed.
    /// </summary>
    public static bool HasReparsePoint(string candidate)
    {
        var current = new DirectoryInfo(Path.GetFullPath(candidate));
        while (current is not null)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return true;
            current = current.Parent;
        }
        return false;
    }
}
