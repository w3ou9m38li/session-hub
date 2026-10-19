using System.Reflection;
using System.Text;

namespace ClaudeLauncher.Infrastructure.Mcp;

/// <summary>
/// The <c>--mcp</c> process: a read-only MCP server over stdin/stdout that lets an agent — Claude
/// Code, Codex, Claude's desktop app — search, list and read the conversations in this tool's
/// catalog. Each agent starts one of its own; it runs beside the window without touching it:
/// no window, no single-instance name, no sync, no setting saved, and no file of the data root
/// held open between calls. Started without WPF.
/// </summary>
public static class McpHost
{
    public const string Argument = "--mcp";

    /// <summary>Where a crash is written, in <see cref="LauncherPaths.DiagnosticsDirectory"/>.</summary>
    public const string CrashFileName = "mcp-crash.txt";

    /// <returns>The process exit code: 0 when the client closed the connection (or there never was one), 1 on a crash.</returns>
    public static int Run(string[] args)
    {
        // Diagnostics only, and never on stdout, which belongs to the protocol. UTF-8 whatever the
        // machine's code page: the agent reading it does not know that page.
        using var log = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        try
        {
            // Before any path is read: while a move of the data folder is unfinished, the folder the
            // window still works in. Never moves anything itself.
            DataRootMigrator.PrepareDataRoot(mayMove: false);

            using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { NewLine = "\n" };
            var version = Version();
            log.WriteLine($"session-hub {version}: MCP server on {LauncherPaths.DataRoot}");
            var tools = new SessionTools(SessionToolsOptions.ForDataRoot());
            return new McpServer(tools, version).Serve(input, output, log);
        }
        catch (Exception ex)
        {
            Crash(ex, log);
            return 1;
        }
    }

    /// <summary>The program's version, as the about box shows it.</summary>
    private static string Version() =>
        (Assembly.GetEntryAssembly() ?? typeof(McpHost).Assembly).GetName().Version?.ToString(3) ?? "0.0.0";

    private static void Crash(Exception ex, TextWriter log)
    {
        try { log.WriteLine($"session-hub MCP server stopped: {ex.GetType().Name}: {ex.Message}"); }
        catch (Exception) { /* nobody is reading the log any more */ }
        try
        {
            Directory.CreateDirectory(LauncherPaths.DiagnosticsDirectory);
            File.WriteAllText(Path.Combine(LauncherPaths.DiagnosticsDirectory, CrashFileName),
                DateTimeOffset.Now.ToString("O") + "\n" + ex, new UTF8Encoding(true));
        }
        catch (Exception)
        {
            // No data root to write to: the log line above is all there is.
        }
    }
}
