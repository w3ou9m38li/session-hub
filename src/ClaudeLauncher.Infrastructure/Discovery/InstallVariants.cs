using System.Diagnostics;
using System.Security;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace ClaudeLauncher.Infrastructure.Discovery;

/// <summary>
/// The other ways the two apps turn up besides the one this launcher was first verified
/// against: Claude from its standalone installer rather than the store, and ChatGPT under
/// another package name from the same publisher. The rules are plain functions over paths and
/// text, so each is tested; only <see cref="ClaudeProtocolCommand"/> and
/// <see cref="SaysItIsAnthropics"/> look at the machine.
/// </summary>
public static class InstallVariants
{
    /// <summary>OpenAI's store publisher id: the part of a package family name that says who made it.</summary>
    public const string OpenAiPublisherId = "2p2nqsd0c76g0";

    /// <summary>Package families the ChatGPT desktop app ships under, preferred first — all OpenAI's.</summary>
    public static readonly IReadOnlyList<string> ChatGptFamilies =
    [
        "OpenAI.Codex_" + OpenAiPublisherId,
        "OpenAI.ChatGPT-Desktop_" + OpenAiPublisherId,
    ];

    /// <summary>An activation id of one of those packages; anything else is not launched.</summary>
    public static bool IsChatGptAumid(string? aumid) =>
        aumid is not null && ChatGptFamilies.Any(f => aumid.StartsWith(f + "!", StringComparison.Ordinal)
                                                      && aumid.Length > f.Length + 1);

    /// <summary>The first application's Id in an AppxManifest.xml — what follows "!" in the activation id.</summary>
    public static string? FirstApplicationId(string manifestXml)
    {
        try
        {
            var id = XDocument.Parse(manifestXml).Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Application")?.Attribute("Id")?.Value;
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>The application Id from an installed package's manifest; null when it cannot be read.</summary>
    public static string? ApplicationIdOf(string? installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation)) return null;
        try
        {
            var manifest = Path.Combine(installLocation, "AppxManifest.xml");
            return File.Exists(manifest) ? FirstApplicationId(File.ReadAllText(manifest)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The program an open command runs: <c>"C:\x\Claude.exe" "%1"</c> → <c>C:\x\Claude.exe</c>.</summary>
    public static string? CommandExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var text = command.Trim();
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }
        var space = text.IndexOf(' ');
        return space < 0 ? text : text[..space];
    }

    /// <summary>
    /// Claude from its standalone installer: the per-user install under
    /// <c>%LOCALAPPDATA%\AnthropicClaude</c> (its launcher first, then the newest <c>app-*</c>
    /// copy), or wherever the claude:// handler points outside WindowsApps. The program must
    /// say it is Anthropic's; its profile is the real <c>%APPDATA%\Claude</c>. Null when there
    /// is no such install.
    /// </summary>
    public static ClaudeInstallation? FindStandaloneClaude(
        string localAppData, string roamingAppData, string? protocolCommand, Func<string, bool> isAnthropics)
    {
        var root = Path.Combine(localAppData, "AnthropicClaude");
        var candidates = new List<string> { Path.Combine(root, "claude.exe") };
        try
        {
            if (Directory.Exists(root))
                candidates.AddRange(Directory.EnumerateDirectories(root, "app-*")
                    .OrderByDescending(d => VersionFromFolder(d))
                    .Select(d => Path.Combine(d, "claude.exe")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // what could be listed is enough
        }
        if (CommandExecutable(protocolCommand) is { } handler
            && !handler.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
            candidates.Add(handler);

        foreach (var exe in candidates)
        {
            if (!File.Exists(exe) || !isAnthropics(exe)) continue;
            var profile = Path.Combine(roamingAppData, "Claude");
            return new ClaudeInstallation
            {
                Found = true,
                Flavor = ClaudeFlavor.Standalone,
                Executable = exe,
                InstallLocation = Path.GetDirectoryName(exe),
                Version = ProductVersion(exe),
                ProfileRoot = Directory.Exists(profile) ? profile : null,
            };
        }
        return null;
    }

    private static Version VersionFromFolder(string folder) =>
        Version.TryParse(Path.GetFileName(folder)["app-".Length..], out var version) ? version : new Version(0, 0);

    private static string? ProductVersion(string exe)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(exe).ProductVersion;
            return string.IsNullOrWhiteSpace(version) ? null : version;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Whether a program's own version information names Anthropic as its maker.</summary>
    public static bool SaysItIsAnthropics(string exe)
    {
        try
        {
            return (FileVersionInfo.GetVersionInfo(exe).CompanyName ?? string.Empty)
                .Contains("Anthropic", StringComparison.OrdinalIgnoreCase);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    /// <summary>What the claude:// protocol runs, per user first; null when nobody registered it.</summary>
    public static string? ClaudeProtocolCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\claude\shell\open\command")
                            ?? Registry.ClassesRoot.OpenSubKey(@"claude\shell\open\command");
            return key?.GetValue(null) as string;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
