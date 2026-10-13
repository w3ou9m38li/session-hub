using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ClaudeLauncher.Infrastructure.Backends;

namespace ClaudeLauncher.Infrastructure.Discovery;

/// <summary>What we know about the installed ChatGPT desktop app (which hosts Codex).</summary>
public sealed record ChatGptInstallation
{
    public required bool Found { get; init; }
    public string? PackageFamilyName { get; init; }
    public string? Version { get; init; }

    /// <summary>Contains the version number, so it is recorded for diagnostics but never launched.</summary>
    public string? InstallLocation { get; init; }

    /// <summary>The version-independent activation id. This is what survives app updates.</summary>
    public string? Aumid { get; init; }

    /// <summary>
    /// The user's own "Codex++（全部任务）" desktop shortcut, when it exists. It launches their
    /// managed copy of the app with its own guard against running two copies, so it is the
    /// route this launcher prefers over the store entry.
    /// </summary>
    public string? ManagedShortcut { get; init; }

    /// <summary>App root of that managed copy, read from the maintenance manifest. Holds a usable <c>codex.exe</c>.</summary>
    public string? ManagedAppRoot { get; init; }

    public string? FailureReason { get; init; }
}

public sealed record InstalledApps(ClaudeInstallation Claude, ChatGptInstallation ChatGpt);

/// <summary>
/// Finds the official Claude and ChatGPT apps through Windows' own package registry: the
/// packaging API first, which answers in about a millisecond, and PowerShell's
/// Get-AppxPackage only if that API cannot answer — PowerShell takes two seconds or more,
/// and every launch used to wait that long before its first sync could start.
///
/// Nothing here depends on a versioned path: install locations move on every update, while
/// package family names and AUMIDs do not. That is the whole reason this launcher keeps
/// working after either app updates itself.
/// </summary>
public static class WindowsAppDiscovery
{
    public const string ChatGptFamilyName = "OpenAI.Codex_2p2nqsd0c76g0";

    /// <summary>Activation id = family name + "!" + the manifest AppId ("App").</summary>
    public const string ChatGptAumid = ChatGptFamilyName + "!App";

    public const string ManagedShortcutName = "Codex++（全部任务）.lnk";

    public static InstalledApps Discover(TimeSpan? timeout = null)
    {
        var apps = FindPackages(timeout);
        // No store package: Claude's standalone installer may have put it elsewhere.
        if (!apps.Claude.Found
            && InstallVariants.FindStandaloneClaude(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                InstallVariants.ClaudeProtocolCommand(), InstallVariants.SaysItIsAnthropics) is { } standalone)
            apps = apps with { Claude = standalone };
        return apps;
    }

    private static InstalledApps FindPackages(TimeSpan? timeout)
    {
        var answered = PackageRegistry.TryFind(WindowsClaudeInstallation.ExpectedFamilyName, out var claude);
        Package? chatgpt = null;
        foreach (var family in InstallVariants.ChatGptFamilies)
        {
            if (!answered) break;
            answered = PackageRegistry.TryFind(family, out var candidate);
            if (candidate is not null)
            {
                chatgpt = candidate;
                break;
            }
        }
        if (answered)
        {
            var found = new List<Package>(2);
            if (claude is not null) found.Add(claude);
            if (chatgpt is not null) found.Add(chatgpt);
            return new InstalledApps(BuildClaude(found), BuildChatGpt(found));
        }

        string json;
        try
        {
            json = RunPowerShell(
                "$a = Get-AppxPackage -Name Claude; $b = Get-AppxPackage -Name OpenAI.Codex; " +
                "$c = Get-AppxPackage -Name OpenAI.ChatGPT-Desktop; " +
                "$rows = @(@($a) + @($b) + @($c) | Where-Object { $null -ne $_ } | ForEach-Object { " +
                "[pscustomobject]@{ name=$_.Name; family=$_.PackageFamilyName; version=[string]$_.Version; install=$_.InstallLocation } }); " +
                "ConvertTo-Json -InputObject $rows -Compress",
                timeout ?? TimeSpan.FromSeconds(30));
        }
        catch (Exception ex)
        {
            return new InstalledApps(
                new ClaudeInstallation { Found = false, FailureReason = ex.Message },
                new ChatGptInstallation { Found = false, FailureReason = ex.Message });
        }

        var packages = ParsePackages(json);
        return new InstalledApps(BuildClaude(packages), BuildChatGpt(packages));
    }

    private sealed record Package(string Name, string? Family, string? Version, string? Install);

    /// <summary>
    /// Windows' packaging API (appmodel.h): the packages of one family installed for this
    /// user, and where they live. Asked by family name — the verified identity — so a package
    /// that merely calls itself "Claude" is never considered.
    /// </summary>
    private static class PackageRegistry
    {
        private const int ErrorSuccess = 0;
        private const int ErrorInsufficientBuffer = 122;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int GetPackagesByPackageFamily(
            string packageFamilyName, ref uint count, [Out] IntPtr[]? packageFullNames,
            ref uint bufferLength, [Out] char[]? buffer);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int GetPackagePathByFullName(string packageFullName, ref uint pathLength, [Out] char[]? path);

        /// <summary>
        /// The newest installed package of <paramref name="family"/>, or null when none is.
        /// False only when the API itself could not answer, so the caller can ask another way.
        /// </summary>
        public static bool TryFind(string family, out Package? package)
        {
            package = null;
            try
            {
                uint count = 0, length = 0;
                var rc = GetPackagesByPackageFamily(family, ref count, null, ref length, null);
                if (rc == ErrorSuccess && count == 0) return true;          // not installed
                if (rc != ErrorInsufficientBuffer || count == 0 || length == 0) return false;

                var pointers = new IntPtr[count];
                var buffer = new char[length];
                if (GetPackagesByPackageFamily(family, ref count, pointers, ref length, buffer) != ErrorSuccess)
                    return false;

                // The full names sit back to back in the buffer, each ending in a NUL.
                string? newest = null;
                Version? newestVersion = null;
                foreach (var fullName in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
                {
                    var version = VersionOf(fullName);
                    if (newest is null || (version is not null && (newestVersion is null || version > newestVersion)))
                        (newest, newestVersion) = (fullName, version);
                }
                if (newest is null) return true;

                package = new Package(family[..family.IndexOf('_')], family, newestVersion?.ToString(), PathOf(newest));
                return true;
            }
            catch (Exception)
            {
                // No packaging API on this Windows, or it failed in some other way: either way
                // PowerShell answers instead — discovery itself must never be what fails a launch.
                return false;
            }
        }

        /// <summary>"Claude_2.2553.13.0_x64__pzs8sxrjxfjjc" → 2.2553.13.0; package names cannot contain "_".</summary>
        private static Version? VersionOf(string fullName)
        {
            var parts = fullName.Split('_');
            return parts.Length >= 2 && Version.TryParse(parts[1], out var version) ? version : null;
        }

        private static string? PathOf(string fullName)
        {
            uint length = 0;
            if (GetPackagePathByFullName(fullName, ref length, null) != ErrorInsufficientBuffer || length == 0) return null;
            var path = new char[length];
            return GetPackagePathByFullName(fullName, ref length, path) == ErrorSuccess
                ? new string(path).TrimEnd('\0')
                : null;
        }
    }

    private static List<Package> ParsePackages(string json)
    {
        var list = new List<Package>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object) Add(root);
            else if (root.ValueKind == JsonValueKind.Array)
                foreach (var item in root.EnumerateArray()) Add(item);
        }
        catch (JsonException)
        {
            // Unparseable output is reported as "not found" by the builders below.
        }
        return list;

        void Add(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object) return;
            list.Add(new Package(Text(e, "name") ?? "", Text(e, "family"), Text(e, "version"), Text(e, "install")));
        }
    }

    private static ClaudeInstallation BuildClaude(List<Package> packages)
    {
        var p = packages.FirstOrDefault(x => x.Name == "Claude");
        if (p is null)
            return new ClaudeInstallation { Found = false, FailureReason = "未找到已注册的 Claude 应用包" };

        // A package that merely calls itself Claude is not enough; the family name must match
        // the identity this launcher was verified against.
        if (!string.Equals(p.Family, WindowsClaudeInstallation.ExpectedFamilyName, StringComparison.Ordinal))
            return new ClaudeInstallation
            {
                Found = false,
                PackageFamilyName = p.Family,
                FailureReason = $"应用包身份不匹配：发现 {p.Family ?? "(null)"}，期望 {WindowsClaudeInstallation.ExpectedFamilyName}",
            };

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profile = Path.Combine(local, "Packages", WindowsClaudeInstallation.ExpectedFamilyName,
            "LocalCache", "Roaming", "Claude");
        // A package that writes to the real AppData (no file-system virtualisation) keeps its profile there.
        if (!Directory.Exists(profile))
            profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");

        return new ClaudeInstallation
        {
            Found = true,
            PackageFamilyName = p.Family,
            Version = p.Version,
            InstallLocation = p.Install,
            Aumid = WindowsClaudeInstallation.ExpectedAumid,
            ProfileRoot = Directory.Exists(profile) ? profile : null,
        };
    }

    private static ChatGptInstallation BuildChatGpt(List<Package> packages)
    {
        var shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ManagedShortcutName);
        var managedShortcut = File.Exists(shortcut) ? shortcut : null;
        var managedRoot = ReadManagedAppRoot();

        // The preferred family first; a package of the right name from another publisher is not OpenAI's.
        var p = InstallVariants.ChatGptFamilies
            .Select(f => packages.FirstOrDefault(x => string.Equals(x.Family, f, StringComparison.Ordinal)))
            .FirstOrDefault(x => x is not null);
        if (p is null)
        {
            var impostor = packages.FirstOrDefault(x => x.Name is "OpenAI.Codex" or "OpenAI.ChatGPT-Desktop");
            return new ChatGptInstallation
            {
                Found = false,
                PackageFamilyName = impostor?.Family,
                ManagedShortcut = managedShortcut,
                ManagedAppRoot = managedRoot,
                FailureReason = impostor is null
                    ? "未找到已注册的 ChatGPT 应用包"
                    : $"应用包身份不匹配：发现 {impostor.Family ?? "(null)"}，不是 OpenAI 发布的包",
            };
        }

        // The activation id is the family plus the manifest's own application id, read rather
        // than assumed, so a package that names its app differently still opens.
        return new ChatGptInstallation
        {
            Found = true,
            PackageFamilyName = p.Family,
            Version = p.Version,
            InstallLocation = p.Install,
            Aumid = p.Family + "!" + (InstallVariants.ApplicationIdOf(p.Install) ?? "App"),
            ManagedShortcut = managedShortcut,
            ManagedAppRoot = managedRoot,
        };
    }

    /// <summary>
    /// The Codex++ maintenance manifest names the managed app root. Read-only, best effort:
    /// its absence only means the store copy is the one to launch.
    /// </summary>
    private static string? ReadManagedAppRoot()
    {
        try
        {
            var manifest = Path.Combine(CodexPaths.DefaultHome(), "maintenance", "codex-sidebar-all", "manifest.json");
            if (!File.Exists(manifest)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            var root = Text(doc.RootElement, "appRoot");
            return root is not null && Directory.Exists(root) ? root : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string RunPowerShell(string script, TimeSpan timeout)
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var shell = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(shell))
            throw new FileNotFoundException("Windows PowerShell is unavailable.", shell);

        var psi = new ProcessStartInfo
        {
            FileName = shell,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start PowerShell.");
        var stdout = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException("Package discovery timed out.");
        }
        return stdout;
    }
}
