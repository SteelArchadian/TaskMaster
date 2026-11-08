using TaskManager.Native;

namespace TaskManager.Engine;

/// <summary>
/// One window at a time, like Task Manager. The running copy owns two named events: "show" (another launch)
/// and "toggle" (the custom shortcut). The running copy is usually elevated, so the events carry a medium
/// integrity label and allow interactive users to signal them; otherwise a normal launch could not reach it.
/// </summary>
public static unsafe class SingleInstance
{
    private const string ShowName = @"Local\SteelArchadian.TaskManager.Show";
    private const string ToggleName = @"Local\SteelArchadian.TaskManager.Toggle";
    // SYSTEM and admins full access; interactive users may wait on and set it; medium label, no write-up.
    private const string Sddl = "D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x100002;;;IU)S:(ML;;NW;;;ME)";

    public static nint ShowEvent { get; private set; }
    public static nint ToggleEvent { get; private set; }

    /// <summary>Signals a running copy. Returns false when none is running.</summary>
    public static bool Signal(bool toggle)
    {
        nint h = Win32.OpenEventW(Win32.EVENT_MODIFY_STATE, 0, toggle ? ToggleName : ShowName);
        if (h == 0) return false;
        // We were just started by the user, so we may hand the foreground to the running window.
        Win32.AllowSetForegroundWindow(Win32.ASFW_ANY);
        Win32.SetEvent(h);
        Kernel32.CloseHandle(h);
        return true;
    }

    /// <summary>Creates the events. Returns false if another copy got there first.</summary>
    public static bool Claim()
    {
        if (Win32.ConvertStringSecurityDescriptorToSecurityDescriptorW(Sddl, 1, out nint sd, 0) == 0)
        {
            Log.Info($"single instance: bad descriptor {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");
            sd = 0;
        }
        try
        {
            var sa = new Win32.SecurityAttributes { Length = sizeof(Win32.SecurityAttributes), SecurityDescriptor = sd };
            ShowEvent = Win32.CreateEventW(&sa, 0, 0, ShowName);
            bool existed = System.Runtime.InteropServices.Marshal.GetLastPInvokeError() == Win32.ERROR_ALREADY_EXISTS;
            ToggleEvent = Win32.CreateEventW(&sa, 0, 0, ToggleName);
            return ShowEvent != 0 && !existed;
        }
        finally
        {
            if (sd != 0) Win32.LocalFree(sd);
        }
    }

    /// <summary>Waits for signals on a background thread and reports them (true = toggle).</summary>
    public static void Listen(Action<bool> onSignal)
    {
        if (ShowEvent == 0) return;
        var t = new Thread(() =>
        {
            nint* handles = stackalloc nint[2];
            handles[0] = ShowEvent;
            handles[1] = ToggleEvent;
            uint count = ToggleEvent != 0 ? 2u : 1u;
            while (true)
            {
                uint r = WaitForMultipleObjects(count, handles, 0, Win32.INFINITE);
                if (r >= count) return;
                onSignal(r == 1);
            }
        }) { IsBackground = true, Name = "Activation" };
        t.Start();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint WaitForMultipleObjects(uint count, nint* handles, int waitAll, uint ms);
}
