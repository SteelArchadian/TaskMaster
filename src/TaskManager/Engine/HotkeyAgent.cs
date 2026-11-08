using System.Diagnostics;
using TaskManager.Native;

namespace TaskManager.Engine;

/// <summary>
/// "--agent" mode: no window, no XAML, just a registered hotkey and a wait. Windows tells us when the shortcut
/// is pressed (RegisterHotKey, no keyboard hook), so it costs nothing between presses. Left/right sides are
/// checked at that moment. Pressing it toggles the running window, or starts the app if it is closed.
/// </summary>
public static unsafe class HotkeyAgent
{
    private const int HotkeyId = 1;

    public static int Run()
    {
        using var mutex = new Mutex(true, @"Local\SteelArchadian.TaskManager.Agent", out bool first);
        if (!first) return 0;

        nint reload = Win32.CreateEventW(null, 0, 0, Shortcuts.AgentReloadName);
        nint exit = Win32.CreateEventW(null, 0, 0, Shortcuts.AgentExitName);
        nint* handles = stackalloc nint[2];
        handles[0] = reload;
        handles[1] = exit;

        var keys = Register();
        while (true)
        {
            uint r = Win32.MsgWaitForMultipleObjects(2, handles, 0, Win32.INFINITE, Win32.QS_ALLINPUT);
            if (r == Win32.WAIT_OBJECT_0) { Win32.UnregisterHotKey(0, HotkeyId); keys = Register(); }
            else if (r == Win32.WAIT_OBJECT_0 + 1) break;
            else if (r == Win32.WAIT_OBJECT_0 + 2)
            {
                Win32.Msg msg;
                while (Win32.PeekMessageW(&msg, 0, 0, 0, Win32.PM_REMOVE) != 0)
                    if (msg.Message == Win32.WM_HOTKEY && msg.WParam == HotkeyId && SidesMatch(keys)) Fire();
            }
            else break;
        }
        Win32.UnregisterHotKey(0, HotkeyId);
        Kernel32.CloseHandle(reload);
        Kernel32.CloseHandle(exit);
        return 0;
    }

    private static List<int> Register()
    {
        var keys = Settings.Load().CustomShortcut;
        int main = keys.FirstOrDefault(k => !Shortcuts.IsModifier(k));
        if (main == 0) return keys;
        uint mods = Win32.MOD_NOREPEAT;
        foreach (var k in keys) mods |= Shortcuts.ModifierFlag(k);
        if (Win32.RegisterHotKey(0, HotkeyId, mods, (uint)main) == 0)
            Log.Info($"agent: could not register {Shortcuts.Describe(keys)} (error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()})");
        else Log.Info($"agent: listening for {Shortcuts.Describe(keys)}");
        return keys;
    }

    // Windows matches Ctrl/Shift/Alt/Win on either side; enforce the sides that were recorded.
    private static bool SidesMatch(List<int> keys)
    {
        foreach (int vk in Shortcuts.SideModifiers)
        {
            bool down = (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;
            if (down != keys.Contains(vk)) return false;
        }
        return true;
    }

    private static void Fire()
    {
        // Receiving the hotkey gives us the right to pass the foreground on.
        if (SingleInstance.Signal(toggle: true)) return;
        try { Process.Start(new ProcessStartInfo(Shortcuts.ExePath) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("agent launch", ex); }
    }
}
