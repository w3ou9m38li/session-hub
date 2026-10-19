using System.Runtime.CompilerServices;
using ClaudeLauncher.Infrastructure.Mcp;
using ClaudeLauncher.Infrastructure.Semantic;

namespace ClaudeLauncher.App;

/// <summary>
/// The process entry. The headless kinds that run for a long time beside the window are told
/// apart here, before WPF is touched: they never load the presentation framework, parse the
/// theme or start a dispatcher. Everything else — the window and the short headless runs
/// (<c>--open</c>, <c>--launch</c>, <c>--draft</c>, <c>--export</c>) — starts exactly as before,
/// through <c>App.OnStartup</c>.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // No WPF type in this body: the JIT would load the presentation framework to compile it.
        if (args.Length >= 1 && args[0] == McpHost.Argument) return McpHost.Run(args);
        if (args.Length >= 1 && args[0] == SemanticWorker.Argument) return SemanticWorker.Run(args);
        return RunWindow();
    }

    /// <summary>What the generated entry point did: the application, its resources, its message loop.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunWindow()
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
