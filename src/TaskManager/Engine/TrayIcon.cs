using System.Runtime.InteropServices;

namespace TaskManager.Engine;

/// <summary>
/// The notification-area icon: left-click opens the window, right-click gives Open / Quit. It lives on a
/// message-only window of its own on the UI thread, so it needs nothing from WinUI. Explorer restarting
/// (TaskbarCreated) puts it back.
/// </summary>
public sealed unsafe class TrayIcon : IDisposable
{
    private const int WM_APP_TRAY = 0x8001, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_COMMAND = 0x0111;
    private const uint NIM_ADD = 0, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    private const int CmdOpen = 1, CmdQuit = 2;
    private static readonly nint HWND_MESSAGE = -3;

    private readonly Action _open, _quit;
    private readonly WndProc _proc; // held so the delegate outlives the window
    private readonly nint _hwnd, _icon;
    private readonly uint _taskbarCreated;
    private bool _shown;

    public TrayIcon(string iconPath, Action open, Action quit)
    {
        _open = open;
        _quit = quit;
        _proc = Proc;
        string cls = "TaskMasterTray";
        fixed (char* name = cls)
        {
            var wc = new WndClass { lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc), hInstance = GetModuleHandleW(null), lpszClassName = name };
            RegisterClassW(ref wc);
        }
        _hwnd = CreateWindowExW(0, cls, "", 0, 0, 0, 0, 0, HWND_MESSAGE, 0, GetModuleHandleW(null), 0);
        _icon = LoadImageW(0, iconPath, 1 /*IMAGE_ICON*/, GetSystemMetrics(49 /*SM_CXSMICON*/), GetSystemMetrics(50), 0x10 /*LR_LOADFROMFILE*/);
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    }

    public void Show()
    {
        if (_shown || _hwnd == 0) return;
        _shown = Notify(NIM_ADD);
    }

    public void Hide()
    {
        if (!_shown) return;
        Notify(NIM_DELETE);
        _shown = false;
    }

    private bool Notify(uint op)
    {
        var d = new NotifyIconData { cbSize = (uint)sizeof(NotifyIconData), hWnd = _hwnd, uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP, uCallbackMessage = WM_APP_TRAY, hIcon = _icon };
        "TaskMaster".AsSpan().CopyTo(new Span<char>(d.szTip, 128));
        return Shell_NotifyIconW(op, ref d);
    }

    private nint Proc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_APP_TRAY)
        {
            int ev = (int)(lParam & 0xFFFF);
            if (ev == WM_LBUTTONUP) _open();
            else if (ev == WM_RBUTTONUP) ShowMenu();
            return 0;
        }
        if (msg == _taskbarCreated && _shown) { _shown = false; Show(); return 0; }
        if (msg == WM_COMMAND)
        {
            if ((int)(wParam & 0xFFFF) == CmdOpen) _open();
            else if ((int)(wParam & 0xFFFF) == CmdQuit) _quit();
            return 0;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        nint menu = CreatePopupMenu();
        AppendMenuW(menu, 0, CmdOpen, "Open TaskMaster");
        AppendMenuW(menu, 0x800 /*MF_SEPARATOR*/, 0, null);
        AppendMenuW(menu, 0, CmdQuit, "Quit");
        SetMenuDefaultItem(menu, CmdOpen, 0);
        GetCursorPos(out var pt);
        // Without the foreground the menu wouldn't close when clicking elsewhere (documented Shell_NotifyIcon quirk).
        SetForegroundWindow(_hwnd);
        TrackPopupMenuEx(menu, 0, pt.X, pt.Y, _hwnd, 0);
        DestroyMenu(menu);
    }

    public void Dispose()
    {
        Hide();
        if (_hwnd != 0) DestroyWindow(_hwnd);
        if (_icon != 0) DestroyIcon(_icon);
    }

    // ---------- interop ----------

    private delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClass
    {
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public char* lpszMenuName, lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
        public uint dwState, dwStateMask;
        public fixed char szInfo[256];
        public uint uVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }

    [DllImport("user32.dll")] private static extern ushort RegisterClassW(ref WndClass wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hwnd, uint msg, nint w, nint l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadImageW(nint inst, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(nint menu, uint flags, nint id, string? text);
    [DllImport("user32.dll")] private static extern bool SetMenuDefaultItem(nint menu, uint item, uint byPos);
    [DllImport("user32.dll")] private static extern bool TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint tpm);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point pt);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(uint msg, ref NotifyIconData data);
}
