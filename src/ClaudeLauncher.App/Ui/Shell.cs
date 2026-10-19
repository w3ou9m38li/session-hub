using System.Diagnostics;
using System.IO;
using System.Windows;

namespace ClaudeLauncher.App;

/// <summary>
/// The few things this tool asks Windows to do on the user's behalf: open a folder, show a
/// file in its folder, put text on the clipboard. Each reports failure as a message instead
/// of throwing, because every caller only wants to tell the user what happened.
/// </summary>
internal static class Shell
{
    /// <summary>Opens a folder or a file with its default handler. Returns an error message, or null on success.</summary>
    public static string? Open(string path)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Opens Explorer at the file with the file selected.</summary>
    public static string? Reveal(string file)
    {
        try
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var psi = new ProcessStartInfo(explorer) { UseShellExecute = false };
            psi.ArgumentList.Add("/select," + file);
            using var _ = Process.Start(psi);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Opens a working directory, checking first that it is there. Run off the UI thread:
    /// a WSL path can take seconds to answer while its distribution starts.
    /// </summary>
    public static async Task<string?> OpenFolderAsync(string folder)
    {
        var exists = await Task.Run(() =>
        {
            try { return Directory.Exists(folder); }
            catch { return false; }
        });
        return exists ? Open(folder) : $"目录不存在或当前无法访问：{folder}";
    }

    /// <summary>
    /// Another program (a clipboard manager, a remote desktop) can hold the clipboard for a
    /// moment; a couple of short retries turn most of those failures into successes.
    /// </summary>
    public static string? Copy(string text)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return null;
            }
            catch (System.Runtime.InteropServices.ExternalException) when (attempt < 2)
            {
                Thread.Sleep(60);       // CLIPBRD_E_CANT_OPEN: someone else has it right now
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }
}
