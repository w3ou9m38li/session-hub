using System.Diagnostics;

namespace ClaudeLauncher.Infrastructure.Launch;

/// <summary>
/// Continues a Gemini CLI session with Gemini CLI's own <c>gemini --resume &lt;id&gt;</c>, in the
/// session's working directory — Gemini keeps its sessions per project, so it only finds one
/// from the folder it was held in.
///
/// Arguments are passed as a list, never as a command string; the id must be a UUID. No prompt
/// is submitted and no approval flag is ever added: the session opens where it left off and
/// waits for the user.
/// </summary>
public static class GeminiSessionLauncher
{
    public static bool IsValidSessionId(string? id) => CodexSessionLauncher.IsValidThreadId(id);

    /// <summary>
    /// Locates Gemini CLI: on PATH, or where npm puts global commands. Nothing else named
    /// "gemini" on disk is trusted.
    /// </summary>
    public static string? FindCliExecutable()
    {
        var candidates = new List<string>();
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                 .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in new[] { "gemini.exe", "gemini.cmd" })
            {
                try { candidates.Add(Path.Combine(dir.Trim(), name)); }
                catch { /* malformed PATH entry */ }
            }
        }
        var npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
        candidates.Add(Path.Combine(npm, "gemini.cmd"));
        return candidates.FirstOrDefault(File.Exists);
    }

    public static ResumeResult Resume(string sessionId, string workingDirectory, string? executable = null)
    {
        if (!IsValidSessionId(sessionId))
            return new ResumeResult(ResumeOutcome.InvalidSessionId, "会话 ID 格式非法，拒绝作为参数传入");

        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            return new ResumeResult(ResumeOutcome.WorkingDirectoryMissing,
                $"工作目录不存在：{workingDirectory}（Gemini CLI 只在会话原来的文件夹里找得到它）");

        var exe = executable ?? FindCliExecutable();
        if (exe is null || !File.Exists(exe))
            return new ResumeResult(ResumeOutcome.ExecutableNotFound,
                "未找到 gemini 命令：Gemini CLI 没有安装，或不在 PATH 里（npm install -g @google/gemini-cli）");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = workingDirectory,
            UseShellExecute = true,   // give the CLI its own console window
        };
        psi.ArgumentList.Add("--resume");
        psi.ArgumentList.Add(sessionId);
        // Intentionally nothing else: no prompt, no approval mode.

        try
        {
            using var _ = Process.Start(psi);
            return new ResumeResult(ResumeOutcome.Started, $"已在 {workingDirectory} 用 gemini --resume 恢复会话");
        }
        catch (Exception ex)
        {
            return new ResumeResult(ResumeOutcome.Failed, ex.Message);
        }
    }
}
