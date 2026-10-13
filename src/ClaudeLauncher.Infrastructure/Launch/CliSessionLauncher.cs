using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ClaudeLauncher.Infrastructure.Launch;

public enum ResumeOutcome
{
    Started,
    ExecutableNotFound,
    InvalidSessionId,
    WorkingDirectoryMissing,
    Failed,
}

public sealed record ResumeResult(ResumeOutcome Outcome, string Message);

/// <summary>
/// Resumes a CLI Code session through Claude's own <c>--resume</c>.
///
/// Arguments are passed as a list, never as a command string, so a title or path can never
/// become shell syntax. No prompt is submitted and no permission-skipping flag is ever added:
/// the resumed session opens exactly where it left off and waits for the user.
/// </summary>
public static partial class CliSessionLauncher
{
    [GeneratedRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex SessionIdPattern();

    public static bool IsValidSessionId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && SessionIdPattern().IsMatch(id);

    /// <summary>
    /// Locates the CLI. Only the per-user install, the CLI that ships inside the official
    /// Claude package, and PATH are consulted; a stray executable named "claude" elsewhere
    /// on disk is not trusted.
    /// </summary>
    /// <param name="profileRoot">
    /// The official app's profile root. It bundles a CLI under
    /// <c>claude-code\{version}\claude.exe</c>, which on many machines is the only one
    /// present — a standalone CLI install is not required to use Claude Code.
    /// The version folder is discovered, never hardcoded, so app updates do not break this.
    /// </param>
    public static string? FindCliExecutable(string? profileRoot = null)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string>
        {
            Path.Combine(local, "Programs", "claude", "claude.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                         ".local", "bin", "claude.exe"),
        };

        if (!string.IsNullOrWhiteSpace(profileRoot))
        {
            var bundled = Path.Combine(profileRoot, "claude-code");
            if (Directory.Exists(bundled))
            {
                try
                {
                    // Newest version folder first; the name is a version, not a sort key we own.
                    candidates.AddRange(Directory.EnumerateDirectories(bundled)
                        .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d))
                        .Select(d => Path.Combine(d, "claude.exe")));
                }
                catch (IOException)
                {
                    // Unreadable package folder: fall through to the other candidates.
                }
            }
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                 .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.Combine(dir.Trim(), "claude.exe")); }
            catch { /* malformed PATH entry */ }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    public static ResumeResult Resume(
        string sessionId, string workingDirectory, string? executable = null, string? profileRoot = null)
    {
        if (!IsValidSessionId(sessionId))
            return new ResumeResult(ResumeOutcome.InvalidSessionId, "会话 ID 格式非法，拒绝作为参数传入");

        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            return new ResumeResult(ResumeOutcome.WorkingDirectoryMissing,
                $"工作目录不存在：{workingDirectory}");

        var exe = executable ?? FindCliExecutable(profileRoot);
        if (exe is null || !File.Exists(exe))
            return new ResumeResult(ResumeOutcome.ExecutableNotFound,
                "未找到已验证的 claude 可执行文件；不执行任意同名程序");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = workingDirectory,
            UseShellExecute = true,   // give the CLI its own console window
        };
        psi.ArgumentList.Add("--resume");
        psi.ArgumentList.Add(sessionId);
        // Intentionally nothing else: no prompt, no --dangerously-skip-permissions.

        try
        {
            using var _ = Process.Start(psi);
            return new ResumeResult(ResumeOutcome.Started, $"已在 {workingDirectory} 恢复会话");
        }
        catch (Exception ex)
        {
            return new ResumeResult(ResumeOutcome.Failed, ex.Message);
        }
    }
}
