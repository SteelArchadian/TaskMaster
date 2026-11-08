using TaskManager.Native;

namespace TaskManager.Engine;

/// <summary>The same Win32 calls Task Manager makes for its process commands. Every handle is closed.</summary>
public static unsafe class ProcessActions
{
    public static bool Terminate(uint pid)
    {
        nint h = Kernel32.OpenProcess(Kernel32.PROCESS_TERMINATE, false, pid);
        if (h == 0) return false;
        try { return Kernel32.TerminateProcess(h, 1); }
        finally { Kernel32.CloseHandle(h); }
    }

    public static bool IsEfficiencyOn(uint pid)
    {
        nint h = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return false;
        try
        {
            var s = new Kernel32.ProcessPowerThrottlingState { Version = Kernel32.PROCESS_POWER_THROTTLING_CURRENT_VERSION };
            if (!Kernel32.GetProcessInformation(h, Kernel32.ProcessPowerThrottling, &s, (uint)sizeof(Kernel32.ProcessPowerThrottlingState)))
                return false;
            return (s.StateMask & Kernel32.PROCESS_POWER_THROTTLING_EXECUTION_SPEED) != 0
                && Kernel32.GetPriorityClass(h) == Kernel32.IDLE_PRIORITY_CLASS;
        }
        finally { Kernel32.CloseHandle(h); }
    }

    /// <summary>Efficiency mode = EcoQoS execution-speed throttling plus idle priority, exactly as Task Manager sets it.</summary>
    public static bool SetEfficiency(uint pid, bool on)
    {
        nint h = Kernel32.OpenProcess(Kernel32.PROCESS_SET_INFORMATION | Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return false;
        try
        {
            var s = new Kernel32.ProcessPowerThrottlingState
            {
                Version = Kernel32.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = Kernel32.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = on ? Kernel32.PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
            };
            bool ok = Kernel32.SetProcessInformation(h, Kernel32.ProcessPowerThrottling, &s, (uint)sizeof(Kernel32.ProcessPowerThrottlingState));
            ok &= Kernel32.SetPriorityClass(h, on ? Kernel32.IDLE_PRIORITY_CLASS : Kernel32.NORMAL_PRIORITY_CLASS);
            return ok;
        }
        finally { Kernel32.CloseHandle(h); }
    }

    /// <summary>Opens Explorer with the file selected.</summary>
    public static void OpenFileLocation(string path)
    {
        if (path.Length == 0 || !File.Exists(path)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
    }

    /// <summary>The file's shell Properties dialog, the same one Explorer shows.</summary>
    public static bool ShowProperties(string path, nint owner)
    {
        if (path.Length == 0) return false;
        fixed (char* verb = "properties\0", file = path + "\0")
        {
            var info = new Win32.ShellExecuteInfo
            {
                Size = sizeof(Win32.ShellExecuteInfo), Mask = Win32.SEE_MASK_INVOKEIDLIST | Win32.SEE_MASK_FLAG_NO_UI,
                Hwnd = owner, Verb = verb, File = file, Show = Win32.SW_SHOW,
            };
            return Win32.ShellExecuteExW(&info) != 0;
        }
    }

    public static void SearchOnline(string imageName, string name)
    {
        string q = Uri.EscapeDataString(imageName.Length > 0 ? $"{imageName} {name}" : name);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"https://www.bing.com/search?q={q}") { UseShellExecute = true })?.Dispose();
    }

    /// <summary>Brings a window forward, restoring it first if minimized.</summary>
    public static void SwitchTo(nint hwnd)
    {
        if (hwnd == 0) return;
        if (User32.IsIconic(hwnd)) User32.ShowWindow(hwnd, Win32.SW_RESTORE);
        User32.SetForegroundWindow(hwnd);
    }

    /// <summary>Full memory dump into %TEMP%\name.DMP, as Task Manager writes it. Returns the path, or throws.</summary>
    public static string CreateDump(uint pid, string imageName)
    {
        string name = Path.GetFileNameWithoutExtension(imageName.Length > 0 ? imageName : $"pid{pid}");
        string path = Path.Combine(Path.GetTempPath(), name + ".DMP");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(Path.GetTempPath(), $"{name} ({i}).DMP");

        nint h = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_INFORMATION | Win32.PROCESS_VM_READ, false, pid);
        if (h == 0) throw new UnauthorizedAccessException(AccessHint([pid]));
        try
        {
            bool ok;
            using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite))
            {
                uint type = Win32.MiniDumpWithFullMemory | Win32.MiniDumpWithHandleData | Win32.MiniDumpWithUnloadedModules
                    | Win32.MiniDumpWithFullMemoryInfo | Win32.MiniDumpWithThreadInfo;
                ok = Win32.MiniDumpWriteDump(h, pid, fs.SafeFileHandle.DangerousGetHandle(), type, 0, 0, 0) != 0;
            }
            if (!ok)
            {
                int err = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                try { File.Delete(path); } catch { } // our own half-written dump, never the user's file
                throw new IOException($"The dump could not be written (error {err & 0xFFFF}).");
            }
            return path;
        }
        finally { Kernel32.CloseHandle(h); }
    }

    // Priority classes in Task Manager's "Set priority" order.
    public static readonly (string name, uint value)[] Priorities =
    [
        ("Realtime", 0x100), ("High", 0x80), ("Above normal", 0x8000), ("Normal", 0x20), ("Below normal", 0x4000), ("Low", 0x40),
    ];

    public static uint GetPriority(uint pid)
    {
        nint h = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return 0;
        try { return Kernel32.GetPriorityClass(h); }
        finally { Kernel32.CloseHandle(h); }
    }

    public static bool SetPriority(uint pid, uint priorityClass)
    {
        nint h = Kernel32.OpenProcess(Kernel32.PROCESS_SET_INFORMATION, false, pid);
        if (h == 0) return false;
        try { return Kernel32.SetPriorityClass(h, priorityClass); }
        finally { Kernel32.CloseHandle(h); }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessAffinityMask(nint process, out nuint processMask, out nuint systemMask);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessAffinityMask(nint process, nuint mask);

    /// <summary>(process mask, system mask), or null when the process can't be opened.</summary>
    public static (ulong process, ulong system)? GetAffinity(uint pid)
    {
        nint h = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return null;
        try { return GetProcessAffinityMask(h, out var pm, out var sm) ? (pm, sm) : null; }
        finally { Kernel32.CloseHandle(h); }
    }

    public static bool SetAffinity(uint pid, ulong mask)
    {
        nint h = Kernel32.OpenProcess(Kernel32.PROCESS_SET_INFORMATION, false, pid);
        if (h == 0) return false;
        try { return SetProcessAffinityMask(h, (nuint)mask); }
        finally { Kernel32.CloseHandle(h); }
    }

    public static string AccessHint(IReadOnlyList<uint> pids) =>
        $"Access is denied for PID {string.Join(", ", pids)}. Run TaskMaster as administrator to change elevated or system processes.";
}
