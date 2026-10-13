using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace ClaudeLauncher.App;

/// <summary>
/// Pictures sent in a conversation: decoded for a thumbnail or at full size, and shown in a
/// window of their own. Only bytes the transcript already holds are ever decoded.
/// </summary>
internal static class Pictures
{
    /// <summary>
    /// The picture as an image, no wider than <paramref name="maxWidth"/> pixels (0: as it is)
    /// and never enlarged; null when the bytes are not a picture Windows can read. Frozen, so
    /// it can be made on any thread.
    /// </summary>
    public static BitmapSource? Decode(byte[] bytes, int maxWidth = 0)
    {
        try
        {
            int width;
            using (var probe = new MemoryStream(bytes, writable: false))
                width = BitmapFrame.Create(probe, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).PixelWidth;

            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = new MemoryStream(bytes, writable: false);
            image.CacheOption = BitmapCacheOption.OnLoad;
            if (maxWidth > 0 && width > maxWidth) image.DecodePixelWidth = maxWidth;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException
                                       or ArgumentException or InvalidOperationException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>The file extension the bytes' own header names.</summary>
    public static string Extension(byte[] bytes) => bytes switch
    {
        [0x89, 0x50, 0x4E, 0x47, ..] => ".png",
        [0xFF, 0xD8, ..] => ".jpg",
        [0x47, 0x49, 0x46, 0x38, ..] => ".gif",
        [0x42, 0x4D, ..] => ".bmp",
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => ".webp",
        _ => ".png",
    };

    /// <summary>
    /// The picture at full size in a window of its own — fitted to the screen, never enlarged —
    /// with copy and save on its context menu. Esc closes it.
    /// </summary>
    public static void Show(Window owner, byte[] bytes, string title)
    {
        if (Decode(bytes) is not { } picture)
        {
            MessageDialog.Show(owner, DialogKind.Info, "无法显示这张图片", "Windows 读不了这种图片格式，可以右键另存后用其他程序打开。");
            return;
        }

        var area = SystemParameters.WorkArea;
        var dpi = VisualTreeHelper.GetDpi(owner);
        var width = picture.PixelWidth / dpi.DpiScaleX;
        var height = picture.PixelHeight / dpi.DpiScaleY;

        var image = new Image
        {
            Source = picture,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            SnapsToDevicePixels = true,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        var window = new Window
        {
            Title = $"{title} · {picture.PixelWidth} × {picture.PixelHeight}",
            Owner = owner,
            Content = image,
            Width = Math.Min(area.Width * 0.9, Math.Max(360, width + 40)),
            Height = Math.Min(area.Height * 0.9, Math.Max(240, height + 60)),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Icon = owner.Icon,
        };
        window.SetResourceReference(Control.BackgroundProperty, "WindowBrush");
        DarkTitleBar.Apply(window);

        var menu = new ContextMenu();
        var copy = new MenuItem { Header = "复制图片" };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetImage(picture); }
            catch (System.Runtime.InteropServices.ExternalException) { /* the clipboard is busy: nothing copied */ }
        };
        var save = new MenuItem { Header = "另存为…" };
        save.Click += (_, _) => SaveAs(window, bytes);
        menu.Items.Add(copy);
        menu.Items.Add(save);
        image.ContextMenu = menu;
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) window.Close();
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) copy.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        };
        window.Show();
    }

    private static void SaveAs(Window owner, byte[] bytes)
    {
        var extension = Extension(bytes);
        var dialog = new SaveFileDialog
        {
            FileName = "图片" + extension,
            DefaultExt = extension,
            Filter = $"图片 (*{extension})|*{extension}|所有文件 (*.*)|*.*",
            AddExtension = true,
        };
        if (dialog.ShowDialog(owner) != true) return;
        try
        {
            File.WriteAllBytes(dialog.FileName, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageDialog.Show(owner, DialogKind.Warning, "保存失败", ex.Message);
        }
    }
}
