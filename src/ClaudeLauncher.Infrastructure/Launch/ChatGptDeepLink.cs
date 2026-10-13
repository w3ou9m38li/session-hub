using System.Diagnostics;
using System.Text.RegularExpressions;
using ClaudeLauncher.Infrastructure.Discovery;

namespace ClaudeLauncher.Infrastructure.Launch;

/// <summary>
/// Opens one Codex task in the ChatGPT desktop app through the app's own <c>codex://</c> link —
/// <c>codex://threads/&lt;thread id&gt;</c>, the link the app itself builds when it opens a task
/// — so the task shows in whichever copy of ChatGPT the user runs.
///
/// <para>The link is never handed to Windows while another copy is running. <c>codex://</c>
/// belongs to the store package, and following it would start the store copy beside a
/// managed one (Codex++), which that setup refuses. Instead the running copy's own program is
/// started once more with the link as its argument: the app keeps one instance, so the new
/// process passes its arguments to the running one and exits — exactly how the Codex++
/// launcher itself forwards a second start. Only when the store copy is the one to use does
/// Windows follow the link, starting the app if needed.</para>
///
/// <para>Launching is all this does: nothing of the app's is read or written.</para>
/// </summary>
public static partial class ChatGptDeepLink
{
    [GeneratedRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex ThreadId();

    /// <summary>The app's link for a task; null for an id that is not a thread id, so nothing else reaches a URL.</summary>
    public static string? BuildThread(string? threadId) =>
        ThreadId().IsMatch(threadId ?? string.Empty) ? $"codex://threads/{threadId!.ToLowerInvariant()}" : null;

    /// <summary>Whether a task id can go into the app's link at all.</summary>
    public static bool CanOpen(string? threadId) => BuildThread(threadId) is not null;

    /// <summary>How long a start through the Codex++ shortcut may take to show its window.</summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    /// <summary>A running ChatGPT window's program, and whether it is the store copy.</summary>
    public sealed record RunningCopy(string Executable, bool IsStoreCopy, int ProcessId);

    public static async Task<DeepLinkResult> OpenAsync(
        ChatGptInstallation app, string threadId, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (BuildThread(threadId) is not { } uri)
            return new DeepLinkResult(DeepLinkOutcome.InvalidSessionId, "任务 ID 格式不合法，拒绝拼入链接");

        // A copy already running takes the link, whichever copy it is.
        if (FindRunning(app) is { } running)
            return running.IsStoreCopy ? Follow(uri, "已请求 ChatGPT（商店版）打开这条任务")
                : Forward(running.Executable, uri, "已请求正在运行的 ChatGPT 打开这条任务");

        // Nothing running and Codex++ set up: start it the way the user does, then hand the link over.
        if (app.ManagedShortcut is { } shortcut && File.Exists(shortcut) && app.ManagedAppRoot is { } root)
        {
            var started = ChatGptLauncher.Open(app);
            if (started.Outcome is not (LaunchOutcome.ActivationRequested or LaunchOutcome.AlreadyRunning))
                return new DeepLinkResult(DeepLinkOutcome.Failed, started.Message);
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < StartTimeout)
            {
                await Task.Delay(500, cancellation);
                if (FindRunningCopy(app) is { HasWindow: true } ready)
                    return ready.Copy.IsStoreCopy ? Follow(uri, "已启动 ChatGPT（商店版）并请求打开这条任务")
                        : Forward(ready.Copy.Executable, uri, "已启动 ChatGPT 并请求打开这条任务");
            }
            return new DeepLinkResult(DeepLinkOutcome.Failed,
                $"已通过「Codex++（全部任务）」启动 ChatGPT，但 {StartTimeout.TotalSeconds:0} 秒内没等到它的窗口，任务没能打开；窗口出来后再点一次即可");
        }

        // The store copy: Windows follows the link, starting the app first when it is not running.
        if (!app.Found)
            return new DeepLinkResult(DeepLinkOutcome.Failed, app.FailureReason ?? "未发现 ChatGPT 应用包");
        return Follow(uri, "已请求 ChatGPT 打开这条任务");
    }

    /// <summary>A running ChatGPT (or Codex) program of the store package or of the managed copy.</summary>
    public static RunningCopy? FindRunning(ChatGptInstallation app) => FindRunningCopy(app)?.Copy;

    /// <summary>The running copy — one showing its window first — and whether its window is up yet.</summary>
    private static (RunningCopy Copy, bool HasWindow)? FindRunningCopy(ChatGptInstallation app)
    {
        (RunningCopy, bool)? best = null;
        foreach (var name in new[] { "ChatGPT", "Codex" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    string? path;
                    bool window;
                    try
                    {
                        path = process.MainModule?.FileName;
                        window = process.MainWindowHandle != IntPtr.Zero;
                    }
                    catch
                    {
                        continue;       // another user's process, or one that just ended
                    }
                    // The window's program itself: never codex.exe under resources\, the app's command-line server.
                    if (path is null || !IsChatGptProgram(app, path)
                        || !string.Equals(Path.GetFileName(path), name + ".exe", StringComparison.OrdinalIgnoreCase)
                        || path.Contains(@"\resources\", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var copy = new RunningCopy(path, IsStoreCopy(path), process.Id);
                    if (window) return (copy, true);
                    best ??= (copy, false);
                }
            }
        }
        return best;
    }

    /// <summary>The store package's folder or the managed copy's: a program of the right family, not anything named ChatGPT.</summary>
    private static bool IsChatGptProgram(ChatGptInstallation app, string path)
    {
        if (app.ManagedAppRoot is { Length: > 0 } root
            && path.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return true;
        return InstallVariants.ChatGptFamilies.Any(family =>
            path.Contains(family[..family.IndexOf('_')], StringComparison.OrdinalIgnoreCase)
            && path.Contains(family[(family.IndexOf('_') + 1)..], StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsStoreCopy(string path) =>
        path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);

    /// <summary>The running copy's program started again with the link: it hands the link to the running one.</summary>
    private static DeepLinkResult Forward(string executable, string uri, string message)
    {
        try
        {
            var psi = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? "",
            };
            psi.ArgumentList.Add(uri);
            QuickOpen.LetTheAppComeForward();
            using var _ = Process.Start(psi);
            return new DeepLinkResult(DeepLinkOutcome.Requested, message, uri);
        }
        catch (Exception ex)
        {
            return new DeepLinkResult(DeepLinkOutcome.Failed, "没能把链接交给 ChatGPT：" + ex.Message, uri);
        }
    }

    /// <summary>Windows follows the link: the store package owns <c>codex://</c>.</summary>
    private static DeepLinkResult Follow(string uri, string message)
    {
        try
        {
            QuickOpen.LetTheAppComeForward();
            using var _ = Process.Start(new ProcessStartInfo { FileName = uri, UseShellExecute = true });
            return new DeepLinkResult(DeepLinkOutcome.Requested, message, uri);
        }
        catch (Exception ex)
        {
            return new DeepLinkResult(DeepLinkOutcome.Failed, ex.Message, uri);
        }
    }
}
