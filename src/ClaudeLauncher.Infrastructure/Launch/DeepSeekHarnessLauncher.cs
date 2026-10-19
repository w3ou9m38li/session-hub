using System.Diagnostics;

namespace ClaudeLauncher.Infrastructure.Launch;

/// <summary>
/// Opens DeepSeek Harness the way the user starts it themselves. dsh runs as a local web server
/// that a start script launches (or finds already running) and then opens in the browser with
/// a sign-in token in the address — so this tool never starts the server or builds that address
/// itself: it runs the user's own entry point, and the token stays inside their script.
///
/// <para>The entry point, first found wins: a shortcut named 「DeepSeek Harness…」 on the desktop
/// or in the Start menu (the user's or everyone's); otherwise a start script in the folder a
/// listed home was deployed in (<c>&lt;deployment&gt;\data</c>) — one whose name says DeepSeek and
/// says start, never one that says stop.</para>
///
/// <para>Launching is the only thing this class does. It never reads or writes dsh's data.</para>
/// </summary>
public static class DeepSeekHarnessLauncher
{
    public const string ShortcutPrefix = "DeepSeek Harness";

    /// <summary>The user's own entry point to DeepSeek Harness; null when there is none.</summary>
    public static string? Find(IEnumerable<string> homes) => Find(homes, ShortcutFolders());

    /// <param name="shortcutFolders">Where to look for a shortcut, and whether in subfolders too (the Start menu's).</param>
    public static string? Find(IEnumerable<string> homes, IEnumerable<(string Folder, bool Deep)> shortcutFolders)
    {
        foreach (var (folder, deep) in shortcutFolders)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
            try
            {
                var hit = Directory.EnumerateFiles(folder, ShortcutPrefix + "*.lnk",
                        new EnumerationOptions { RecurseSubdirectories = deep, MaxRecursionDepth = 3, IgnoreInaccessible = true })
                    .Where(f => !SaysStop(Path.GetFileName(f)))
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (hit is not null) return hit;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        foreach (var home in homes)
        {
            var trimmed = Path.TrimEndingDirectorySeparator(home);
            if (!string.Equals(Path.GetFileName(trimmed), "data", StringComparison.OrdinalIgnoreCase)) continue;
            var deployment = Path.GetDirectoryName(trimmed);
            if (deployment is null || !Directory.Exists(deployment)) continue;
            try
            {
                var script = Directory.EnumerateFiles(deployment)
                    .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".cmd" or ".bat" or ".lnk")
                    .Where(f => IsStartScript(Path.GetFileName(f)))
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (script is not null) return script;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>A start script's name: it names DeepSeek and says start, and does not say stop.</summary>
    public static bool IsStartScript(string name) =>
        name.Contains("DeepSeek", StringComparison.OrdinalIgnoreCase)
        && (name.Contains("启动", StringComparison.Ordinal) || name.Contains("打开", StringComparison.Ordinal)
            || name.StartsWith("start", StringComparison.OrdinalIgnoreCase) || name.StartsWith("launch", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("open", StringComparison.OrdinalIgnoreCase))
        && !SaysStop(name);

    private static bool SaysStop(string name) =>
        name.Contains("停止", StringComparison.Ordinal) || name.Contains("关闭", StringComparison.Ordinal)
        || name.Contains("stop", StringComparison.OrdinalIgnoreCase) || name.Contains("uninstall", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<(string, bool)> ShortcutFolders()
    {
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), false);
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), false);
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.Programs), true);
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), true);
    }

    /// <summary>What the entry point is called, for labels: the shortcut's or script's name without its extension.</summary>
    public static string NameOf(string target) => Path.GetFileNameWithoutExtension(target);

    public static LaunchResult Open(string? target)
    {
        if (target is null || !File.Exists(target))
            return new LaunchResult(LaunchOutcome.PackageNotFound,
                "没找到 DeepSeek Harness 的启动方式：桌面和开始菜单里没有名字以「DeepSeek Harness」开头的快捷方式，"
                + "已添加的会话位置旁边也没有它的启动脚本");
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(target)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(target) ?? "",
            });
            return new LaunchResult(LaunchOutcome.ActivationRequested,
                $"已通过「{NameOf(target)}」请求启动 DeepSeek Harness：它会在浏览器里打开，已在运行时直接打开网页");
        }
        catch (Exception ex)
        {
            return new LaunchResult(LaunchOutcome.ShellUnavailable, "启动失败：" + ex.Message);
        }
    }
}
