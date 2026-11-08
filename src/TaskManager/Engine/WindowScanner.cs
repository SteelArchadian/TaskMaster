using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TaskManager.Native;

namespace TaskManager.Engine;

/// <summary>
/// Finds processes that own a top-level app window, the same test Task Manager uses to put a
/// process under "Apps": visible, unowned, not a tool window, not cloaked, has a title.
/// </summary>
public static unsafe class WindowScanner
{
    [ThreadStatic] private static Dictionary<uint, List<nint>>? t_result;

    public static Dictionary<uint, List<nint>> Scan()
    {
        t_result = new Dictionary<uint, List<nint>>();
        User32.EnumWindows(&Callback, 0);
        var r = t_result;
        t_result = null;
        return r;
    }

    [UnmanagedCallersOnly]
    private static int Callback(nint hwnd, nint lParam)
    {
        if (!User32.IsWindowVisible(hwnd)) return 1;
        if (User32.GetWindow(hwnd, User32.GW_OWNER) != 0) return 1;
        if ((User32.GetWindowLongPtrW(hwnd, User32.GWL_EXSTYLE) & User32.WS_EX_TOOLWINDOW) != 0) return 1;
        if (User32.GetWindowTextLengthW(hwnd) == 0) return 1;
        int cloaked = 0;
        if (User32.DwmGetWindowAttribute(hwnd, User32.DWMWA_CLOAKED, &cloaked, sizeof(int)) == 0 && cloaked != 0) return 1;

        User32.GetWindowThreadProcessId(hwnd, out uint pid);
        var map = t_result!;
        if (!map.TryGetValue(pid, out var list)) map[pid] = list = new List<nint>(1);
        list.Add(hwnd);
        return 1;
    }
}
