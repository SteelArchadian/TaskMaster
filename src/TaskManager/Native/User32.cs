using System.Runtime.InteropServices;

namespace TaskManager.Native;

internal static partial class User32
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial nint GetWindow(nint hwnd, uint cmd);

    [LibraryImport("user32.dll")]
    public static partial int GetWindowTextLengthW(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    public static unsafe partial int GetWindowText(nint hwnd, char* text, int max);

    public static unsafe string GetWindowTitle(nint hwnd)
    {
        char* buf = stackalloc char[512];
        int n = GetWindowText(hwnd, buf, 512);
        return n > 0 ? new string(buf, 0, n) : "";
    }

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    [LibraryImport("user32.dll")]
    public static partial nint GetWindowLongPtrW(nint hwnd, int index);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint hwnd, int cmd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hwnd);

    [LibraryImport("dwmapi.dll")]
    public static unsafe partial int DwmGetWindowAttribute(nint hwnd, uint attr, void* value, uint size);

    public const uint GW_OWNER = 4;
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x80;
    public const uint DWMWA_CLOAKED = 14;
}
