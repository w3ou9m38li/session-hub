using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ClaudeLauncher.Infrastructure.Launch;

/// <summary>
/// Resumes a Codex thread through Codex's own <c>codex resume &lt;id&gt;</c>, the counterpart
/// of <c>claude --resume</c>.
///
/// Arguments are passed as a list, never as a command string, so a title or path can never
/// become shell syntax. No prompt is submitted and no permission flag is ever added: the
/// resumed session opens exactly where it left off and waits for the user.
/// </summary>
public static partial class CodexSessionLauncher
{
    [GeneratedRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex ThreadIdPattern();

    public static bool IsValidThreadId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && ThreadIdPattern().IsMatch(id);

    /// <summary>
    /// Locates a Codex CLI. Only PATH, the per-user store alias, the user's managed Codex++
    /// copy and the store package's own bundle are consulted; a stray executable named
    /// "codex" elsewhere on disk is not trusted.
    /// </summary>
    /// <param name="managedAppRoot">
    /// App root of the managed copy. Preferred over the store bundle because executables under
    /// <c>WindowsApps</c> are often refused when started from outside their package.
    /// </param>
    public static string? FindCliExecutable(string? managedAppRoot = null, string? storeInstallLocation = null)
    {
        var candidates = new List<string>();

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                 .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.Combine(dir.Trim(), "codex.exe")); }
            catch { /* malformed PATH entry */ }
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(local, "Microsoft", "WindowsApps", "codex.exe"));

        if (!string.IsNullOrWhiteSpace(managedAppRoot))
            candidates.Add(Path.Combine(managedAppRoot, "resources", "codex.exe"));

        if (!string.IsNullOrWhiteSpace(storeInstallLocation))
            candidates.Add(Path.Combine(storeInstallLocation, "app", "resources", "codex.exe"));

        return candidates.FirstOrDefault(File.Exists);
    }

    public static ResumeResult Resume(
        string threadId, string workingDirectory, string? executable = null,
        string? managedAppRoot = null, string? storeInstallLocation = null)
    {
        if (!IsValidThreadId(threadId))
            return new ResumeResult(ResumeOutcome.InvalidSessionId, "任务 ID 格式非法，拒绝作为参数传入");

        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            return new ResumeResult(ResumeOutcome.WorkingDirectoryMissing,
                $"工作目录不存在：{workingDirectory}");

        var exe = executable ?? FindCliExecutable(managedAppRoot, storeInstallLocation);
        if (exe is null || !File.Exists(exe))
            return new ResumeResult(ResumeOutcome.ExecutableNotFound,
                "未找到已验证的 codex 可执行文件；不执行任意同名程序");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = workingDirectory,
            UseShellExecute = true,   // give the CLI its own console window
        };
        psi.ArgumentList.Add("resume");
        psi.ArgumentList.Add(threadId);
        // Intentionally nothing else: no prompt, no sandbox or approval overrides.

        try
        {
            using var _ = Process.Start(psi);
            return new ResumeResult(ResumeOutcome.Started, $"已在 {workingDirectory} 用 codex resume 恢复任务");
        }
        catch (Exception ex)
        {
            return new ResumeResult(ResumeOutcome.Failed, ex.Message);
        }
    }
}
