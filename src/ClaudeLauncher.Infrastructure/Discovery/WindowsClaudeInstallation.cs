namespace ClaudeLauncher.Infrastructure.Discovery;

/// <summary>How Claude is installed: the store package, or the standalone installer's copy.</summary>
public enum ClaudeFlavor { Store, Standalone }

/// <summary>What we know about the installed official Claude, discovered at run time.</summary>
public sealed record ClaudeInstallation
{
    public required bool Found { get; init; }

    public ClaudeFlavor Flavor { get; init; } = ClaudeFlavor.Store;

    /// <summary>The standalone copy's program; the store package is started by its activation id instead.</summary>
    public string? Executable { get; init; }
    public string? PackageFamilyName { get; init; }
    public string? Version { get; init; }

    /// <summary>Contains the version number, so it is recorded for diagnostics but never launched.</summary>
    public string? InstallLocation { get; init; }

    /// <summary>The version-independent activation id. This is what survives app updates.</summary>
    public string? Aumid { get; init; }

    /// <summary>MSIX profile root; the real location behind the virtualised Roaming\Claude path.</summary>
    public string? ProfileRoot { get; init; }

    public string? FailureReason { get; init; }
}

/// <summary>
/// The verified identity of the official Claude package. Discovery itself lives in
/// <see cref="WindowsAppDiscovery"/>, which finds Claude and ChatGPT in one round trip;
/// this entry point remains for callers that only need Claude.
/// </summary>
public static class WindowsClaudeInstallation
{
    /// <summary>The package family this launcher is verified against.</summary>
    public const string ExpectedFamilyName = "Claude_pzs8sxrjxfjjc";

    /// <summary>Activation id = family name + "!" + the manifest AppId ("Claude").</summary>
    public const string ExpectedAumid = ExpectedFamilyName + "!Claude";

    public static ClaudeInstallation Discover(TimeSpan? timeout = null) =>
        WindowsAppDiscovery.Discover(timeout).Claude;
}
