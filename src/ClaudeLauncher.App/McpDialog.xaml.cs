using System.IO;
using System.Windows;
using System.Windows.Controls;
using ClaudeLauncher.Infrastructure.Mcp;

namespace ClaudeLauncher.App;

/// <summary>
/// How to let Claude Code, Codex or Claude's desktop app search and read this history: what the
/// MCP server is, where this program is, and the lines to put into each one's configuration —
/// to copy, never written there by this program.
/// </summary>
public partial class McpDialog : Window
{
    private McpDialog(string exe)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        ExePath.Text = exe;
        PathNote.Text = "配置里写的是这个程序文件的完整路径：程序挪了位置，要把配置里的路径一起改掉。"
                        + (McpConfig.NameCarriesVersion(exe)
                            ? $"\n这个文件名带版本号（{Path.GetFileName(exe)}）：换了新版本，就要把路径改成新文件的；"
                              + "也可以把程序改名成不带版本号的固定名字，再照下面配置。"
                            : "")
                        + "\n替换、挪动或删掉这个文件之前，先退出 Claude 桌面版和正在运行的 Claude Code / Codex 会话："
                        + "它们各自开着一个会话管家进程，占着这个文件。";
        ClaudeCodeSnippet.Text = McpConfig.ClaudeCode(exe);
        CodexSnippet.Text = McpConfig.Codex(exe);
        ClaudeDesktopSnippet.Text = McpConfig.ClaudeDesktop(exe);
    }

    public static void Show(Window owner)
    {
        var dialog = new McpDialog(Environment.ProcessPath ?? "ClaudeLauncher.App.exe") { Owner = owner };
        dialog.ShowDialog();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TextBox snippet) return;
        var error = Shell.Copy(snippet.Text);
        StatusLine.Text = error is null ? "已复制。" : "复制失败：" + error;
    }
}
