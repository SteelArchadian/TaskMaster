using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace TaskManager.Views;

/// <summary>
/// Test hook: <c>--snapshot out.png [--delay ms] [--page Name]</c> renders the window's XAML to a PNG and exits.
/// Lets automated checks see the UI even when the desktop is locked or the window is covered.
/// </summary>
public static class SnapshotMode
{
    public static (string path, int delay, string? page)? Parse()
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--snapshot");
        if (i < 0 || i + 1 >= args.Length) return null;
        int d = Array.IndexOf(args, "--delay");
        int delay = d >= 0 && d + 1 < args.Length && int.TryParse(args[d + 1], out var ms) ? ms : 4000;
        int p = Array.IndexOf(args, "--page");
        return (args[i + 1], delay, p >= 0 && p + 1 < args.Length ? args[p + 1] : null);
    }

    public static async Task RunAsync(Window window, string path, int delay)
    {
        await Task.Delay(delay);
        if (Environment.GetCommandLineArgs().Contains("--selftest") && ProcessesPage.Current is { } page)
        {
            try { page.SelfTest(); } catch (Exception ex) { Engine.Log.Error("selftest", ex); }
            await Task.Delay(2500);
        }
        try
        {
            var root = (UIElement)window.Content;
            // Mica isn't part of the XAML tree, so paint its fallback color to judge contrast correctly.
            if (root is Microsoft.UI.Xaml.Controls.Panel panel)
                panel.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    panel.ActualTheme == ElementTheme.Light
                        ? Windows.UI.Color.FromArgb(255, 0xF3, 0xF3, 0xF3)
                        : Windows.UI.Color.FromArgb(255, 0x20, 0x20, 0x20));
            await Task.Delay(200);
            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(root);
            var pixels = await rtb.GetPixelsAsync();
            using var stream = new InMemoryRandomAccessStream();
            var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, 96, 96, pixels.ToArray());
            await enc.FlushAsync();
            var bytes = new byte[stream.Size];
            stream.Seek(0);
            await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
            await File.WriteAllBytesAsync(path, bytes);
        }
        catch (Exception ex)
        {
            Engine.Log.Error("snapshot", ex);
        }
        // Closing the last window ends the app; Application.Exit() faults inside CoreMessaging on unpackaged apps.
        window.Close();
    }
}
