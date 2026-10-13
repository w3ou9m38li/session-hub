using System.Diagnostics;
using ClaudeLauncher.Infrastructure.Discovery;

namespace ClaudeLauncher.Infrastructure.Launch;

/// <summary>
/// Opens the ChatGPT desktop app (the home of Codex).
///
/// <para>Two routes, tried in this order. First, the user's own "Codex++（全部任务）" shortcut
/// when it exists: it starts their managed copy and carries its own guard against running
/// two copies at once, so honouring it means never fighting the user's setup. Otherwise,
/// shell activation of the store package through its AUMID, exactly as for Claude —
/// version-independent, and never the versioned WindowsApps path.</para>
///
/// <para>Launching is the only thing this class does. It never reads or writes the app's data.</para>
/// </summary>
public static class ChatGptLauncher
{
    public static LaunchResult Open(ChatGptInstallation app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (app.ManagedShortcut is { } shortcut && File.Exists(shortcut))
        {
            try
            {
                using var _ = Process.Start(new ProcessStartInfo(shortcut) { UseShellExecute = true });
                return new LaunchResult(LaunchOutcome.ActivationRequested,
                    "已通过「Codex++（全部任务）」快捷方式请求启动 ChatGPT；已在运行时它会切到现有窗口");
            }
            catch (Exception ex)
            {
                return new LaunchResult(LaunchOutcome.ShellUnavailable, "快捷方式启动失败：" + ex.Message);
            }
        }

        if (!app.Found)
            return new LaunchResult(LaunchOutcome.PackageNotFound,
                app.FailureReason ?? "未发现 ChatGPT 应用包");

        if (!InstallVariants.IsChatGptAumid(app.Aumid))
            return new LaunchResult(LaunchOutcome.IdentityMismatch, $"启动目标身份未验证：{app.Aumid}");

        var existing = FindRunning();

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var explorer = Path.GetFullPath(Path.Combine(windows, "explorer.exe"));
        if (!File.Exists(explorer))
            return new LaunchResult(LaunchOutcome.ShellUnavailable, "找不到 Windows shell (explorer.exe)");

        var psi = new ProcessStartInfo { FileName = explorer, UseShellExecute = false };
        psi.ArgumentList.Add("shell:AppsFolder\\" + app.Aumid);

        try
        {
            using var _ = Process.Start(psi);
        }
        catch (Exception ex)
        {
            return new LaunchResult(LaunchOutcome.ShellUnavailable, ex.Message);
        }

        return existing is null
            ? new LaunchResult(LaunchOutcome.ActivationRequested, "已请求启动 ChatGPT（商店版）")
            : new LaunchResult(LaunchOutcome.AlreadyRunning, "ChatGPT 已在运行，已请求切换到前台", existing.Id);
    }

    /// <summary>
    /// A running ChatGPT, store copy or managed copy: both keep the package's folder name in
    /// their path. Null when it cannot be determined; this launcher never kills either.
    /// </summary>
    public static Process? FindRunning()
    {
        foreach (var process in Process.GetProcessesByName("ChatGPT"))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (path is not null && InstallVariants.ChatGptFamilies.Any(f =>
                        path.Contains(f[..f.IndexOf('_')], StringComparison.OrdinalIgnoreCase)))
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
