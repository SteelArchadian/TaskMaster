using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TaskManager.Engine;
using TaskManager.Native;

namespace TaskManager.Views;

/// <summary>
/// Small icons per exe, extracted once on a dedicated low-priority STA thread (the shell needs COM)
/// and turned into bitmaps on the UI thread only when a row asks. Never blocks a frame.
/// </summary>
public sealed unsafe class IconCache
{
    private const int Size = 16;
    private readonly DispatcherQueue _ui;
    private readonly ConcurrentDictionary<string, ImageSource?> _done = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<Action<ImageSource?>>> _waiting = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingCollection<string> _queue = new();

    public IconCache(DispatcherQueue ui)
    {
        _ui = ui;
        var t = new Thread(Worker) { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "Icons" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    /// <summary>Calls <paramref name="onReady"/> on the UI thread, now if cached or later when extracted.</summary>
    public void Get(string path, Action<ImageSource?> onReady)
    {
        string key = path.Length == 0 ? "?default" : path;
        if (_done.TryGetValue(key, out var img)) { onReady(img); return; }
        bool first = false;
        var list = _waiting.GetOrAdd(key, _ => { first = true; return new List<Action<ImageSource?>>(); });
        lock (list) list.Add(onReady);
        if (first) _queue.Add(key);
    }

    private void Worker()
    {
        Shell.CoInitializeEx(0, Shell.COINIT_APARTMENTTHREADED);
        foreach (var key in _queue.GetConsumingEnumerable())
        {
            byte[]? pixels = null;
            try { pixels = Extract(key); }
            catch (Exception ex) { Log.Error("icon " + key, ex); }

            _ui.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                ImageSource? src = null;
                if (pixels != null)
                {
                    var wb = new WriteableBitmap(Size, Size);
                    using (var s = wb.PixelBuffer.AsStream()) s.Write(pixels, 0, pixels.Length);
                    wb.Invalidate();
                    src = wb;
                }
                _done[key] = src;
                if (_waiting.TryRemove(key, out var list))
                    lock (list) foreach (var cb in list) cb(src);
            });
        }
    }

    private static byte[]? Extract(string key)
    {
        Shell.ShFileInfo info;
        uint flags = Shell.SHGFI_ICON | Shell.SHGFI_SMALLICON;
        string path = key;
        if (key == "?default") { path = "x.exe"; flags |= Shell.SHGFI_USEFILEATTRIBUTES; }
        if (Shell.SHGetFileInfo(path, Shell.FILE_ATTRIBUTE_NORMAL, &info, (uint)sizeof(Shell.ShFileInfo), flags) == 0 || info.hIcon == 0)
            return null;

        nint dc = Shell.CreateCompatibleDC(0);
        var bih = new Shell.BitmapInfoHeader
        {
            Size = (uint)sizeof(Shell.BitmapInfoHeader), Width = Size, Height = -Size, Planes = 1, BitCount = 32,
        };
        nint dib = Shell.CreateDIBSection(dc, &bih, 0, out void* bits, 0, 0);
        try
        {
            nint old = Shell.SelectObject(dc, dib);
            Shell.DrawIconEx(dc, 0, 0, info.hIcon, Size, Size, 0, 0, Shell.DI_NORMAL);
            Shell.SelectObject(dc, old);

            var result = new byte[Size * Size * 4];
            new ReadOnlySpan<byte>(bits, result.Length).CopyTo(result);

            // Old icons without an alpha channel draw opaque pixels with alpha 0.
            bool hasAlpha = false;
            for (int i = 3; i < result.Length; i += 4) if (result[i] != 0) { hasAlpha = true; break; }
            if (!hasAlpha)
                for (int i = 0; i < result.Length; i += 4)
                    if ((result[i] | result[i + 1] | result[i + 2]) != 0) result[i + 3] = 255;
            return result;
        }
        finally
        {
            Shell.DeleteObject(dib);
            Shell.DeleteDC(dc);
            Shell.DestroyIcon(info.hIcon);
        }
    }
}
