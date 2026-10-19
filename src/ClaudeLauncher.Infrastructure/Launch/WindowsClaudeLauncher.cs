using System.Diagnostics;
using ClaudeLauncher.Infrastructure.Discovery;

namespace ClaudeLauncher.Infrastructure.Launch;

public enum LaunchOutcome
{
    /// <summary>Shell activation was requested. NOT proof that Claude is interactive yet.</summary>
    ActivationRequested,
    /// <summary>Claude was already running; we asked the shell to bring it forward.</summary>
    AlreadyRunning,
    PackageNotFound,
    IdentityMismatch,
    ShellUnavailable,
}

public sealed record LaunchResult(LaunchOutcome Outcome, string Message, int? OfficialProcessId = null);

/// <summary>
/// Opens the official Claude through Windows shell activation.
///
/// Update resilience: activation goes through <c>shell:AppsFolder\{AUMID}</c>, which Windows
/// resolves from the package registry. The versioned WindowsApps path is never used, so a
/// Claude update does not break this launcher. In exchange, the AUMID is checked against the
/// verified identity rather than accepted from configuration.
/// </summary>
public static class WindowsClaudeLauncher
{
    public static LaunchResult Activate(ClaudeInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);

        if (!installation.Found)
            return new LaunchResult(LaunchOutcome.PackageNotFound,
                installation.FailureReason ?? "未发现官方 Claude 应用包");

        if (installation.Flavor == ClaudeFlavor.Standalone) return ActivateStandalone(installation);

        if (!string.Equals(installation.Aumid, WindowsClaudeInstallation.ExpectedAumid, StringComparison.Ordinal))
            return new LaunchResult(LaunchOutcome.IdentityMismatch,
                $"启动目标身份未验证：{installation.Aumid}");

        var existing = FindOfficialProcess();

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var explorer = Path.GetFullPath(Path.Combine(windows, "explorer.exe"));
        if (!File.Exists(explorer))
            return new LaunchResult(LaunchOutcome.ShellUnavailable, "找不到 Windows shell (explorer.exe)");

        var psi = new ProcessStartInfo { FileName = explorer, UseShellExecute = false };
        psi.ArgumentList.Add("shell:AppsFolder\\" + WindowsClaudeInstallation.ExpectedAumid);

        try
        {
            using var _ = Process.Start(psi);
        }
        catch (Exception ex)
        {
            return new LaunchResult(LaunchOutcome.ShellUnavailable, ex.Message);
        }

        // explorer.exe exits immediately after handing the activation to the shell; its exit
        // code says nothing about Claude. Whether Claude is interactive is a separate check.
        return existing is null
            ? new LaunchResult(LaunchOutcome.ActivationRequested, "已请求启动官方 Claude")
            : new LaunchResult(LaunchOutcome.AlreadyRunning, "官方 Claude 已在运行，已请求切换到前台", existing.Id);
    }

    /// <summary>
    /// The standalone installer's Claude: its own program, started directly — checked again
    /// that it is still there and still says it is Anthropic's.
    /// </summary>
    private static LaunchResult ActivateStandalone(ClaudeInstallation installation)
    {
        if (installation.Executable is not { } exe || !File.Exists(exe))
            return new LaunchResult(LaunchOutcome.PackageNotFound, "独立安装版 Claude 的程序文件不在了，请重新安装或同步后重试");
        if (!InstallVariants.SaysItIsAnthropics(exe))
            return new LaunchResult(LaunchOutcome.IdentityMismatch, $"启动目标身份未验证：{exe}");

        var existing = FindStandaloneProcess(installation);
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty,
            });
        }
        catch (Exception ex)
        {
            return new LaunchResult(LaunchOutcome.ShellUnavailable, ex.Message);
        }
        return existing is null
            ? new LaunchResult(LaunchOutcome.ActivationRequested, "已请求启动官方 Claude（独立安装版）")
            : new LaunchResult(LaunchOutcome.AlreadyRunning, "官方 Claude 已在运行，已请求切换到前台", existing.Id);
    }

    /// <summary>A running standalone Claude: a Claude process inside the same install folder.</summary>
    private static Process? FindStandaloneProcess(ClaudeInstallation installation)
    {
        var root = installation.InstallLocation is { } location
            ? (Path.GetFileName(location).StartsWith("app-", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(location) : location)
            : null;
        if (root is null) return null;
        foreach (var process in Process.GetProcessesByName("Claude"))
        {
            try
            {
                if (process.MainModule?.FileName is { } path && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return process;
            }
            catch
            {
                // Access denied on another user's process; treat as not ours.
            }
        }
        return null;
    }

    /// <summary>
    /// Finds a running official Claude by main-module path inside the packaged install root.
    /// Returns null when it cannot be determined; the launcher never kills or restarts Claude.
    /// </summary>
    public static Process? FindOfficialProcess()
    {
        foreach (var process in Process.GetProcessesByName("Claude"))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (path is not null &&
                    path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase) &&
                    path.Contains("pzs8sxrjxfjjc", StringComparison.OrdinalIgnoreCase))
                    return process;
            }
            catch
            {
                // Access denied on another user's process; treat as not ours.
            }
        }
        return null;
    }
}
