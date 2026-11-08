using System.Diagnostics;
using Microsoft.Win32;
using TaskManager.Native;

namespace TaskManager.Engine;

/// <summary>
/// The two ways to open this app from the keyboard:
/// 1. Ctrl+Shift+Esc: Windows always starts taskmgr.exe for it, so an Image File Execution Options "Debugger"
///    value sends that launch here instead (the same documented switch Process Explorer uses). Fully undone
///    by turning the setting off, which restores whatever value was there before.
/// 2. A custom shortcut: a small agent (this exe with --agent) registers it with Windows and toggles the window.
///    It starts with Windows only while a custom shortcut is set.
/// </summary>
public static class Shortcuts
{
    private const string IfeoKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\taskmgr.exe";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "TaskManagerShortcut";
    public const string AgentReloadName = @"Local\SteelArchadian.TaskManager.AgentReload";
    public const string AgentExitName = @"Local\SteelArchadian.TaskManager.AgentExit";

    public static string ExePath => Environment.ProcessPath!;
    private static string DebuggerValue => $"\"{ExePath}\"";

    // ---------- Ctrl+Shift+Esc ----------

    public static string? CurrentDebugger()
    {
        using var k = Registry.LocalMachine.OpenSubKey(IfeoKey);
        return k?.GetValue("Debugger") as string;
    }

    public static bool IsReplacing() =>
        string.Equals(CurrentDebugger()?.Trim('"'), ExePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Turns the takeover on or off. Needs admin; returns an error message or null.</summary>
    public static string? SetReplace(Settings s, bool on)
    {
        if (!Elevation.IsElevated) return "Run TaskMaster as administrator to change this.";
        try
        {
            if (on)
            {
                if (IsReplacing()) return null;
                string? before = CurrentDebugger();
                // Remember a foreign value (another tool's takeover) so turning ours off gives it back.
                if (before != null && before != s.ReplacedWith && File.Exists(before.Trim().Trim('"'))) s.PreviousDebugger = before;
                using var k = Registry.LocalMachine.CreateSubKey(IfeoKey, writable: true);
                k.SetValue("Debugger", DebuggerValue, RegistryValueKind.String);
                s.ReplacedWith = DebuggerValue;
                s.Save();
                Log.Info($"ifeo on (was '{before}')");
            }
            else
            {
                using var k = Registry.LocalMachine.OpenSubKey(IfeoKey, writable: true);
                if (k == null || !IsReplacing()) return null;
                if (!string.IsNullOrEmpty(s.PreviousDebugger)) k.SetValue("Debugger", s.PreviousDebugger, RegistryValueKind.String);
                else k.DeleteValue("Debugger", throwOnMissingValue: false);
                Log.Info($"ifeo off (restored '{s.PreviousDebugger}')");
                s.PreviousDebugger = null;
                s.ReplacedWith = null;
                s.Save();
            }
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("ifeo", ex);
            return ex.Message;
        }
    }

    /// <summary>True when Windows started us in taskmgr.exe's place for Ctrl+Shift+Esc (it passes "/2"; Ctrl+Alt+Del is
    /// "/3", the taskbar "/4", Start "/7").</summary>
    public static bool IsCtrlShiftEscLaunch(string[] args) =>
        args.Length >= 2 && args[0].EndsWith("taskmgr.exe", StringComparison.OrdinalIgnoreCase) && args[1] == "/2";

    /// <summary>
    /// Starts Windows' own Task Manager despite the takeover. A process started as a debugger target skips the
    /// Debugger value, so it starts that way and is let go straight away. Needs admin like Task Manager itself;
    /// false if that didn't work, and the caller opens this app instead.
    /// </summary>
    public static unsafe bool StartRealTaskManager(string switches)
    {
        string exe = Path.Combine(Environment.SystemDirectory, "taskmgr.exe");
        var cmd = ($"\"{exe}\" {switches}".TrimEnd() + "\0").ToCharArray();
        var si = new Win32.StartupInfo { Cb = sizeof(Win32.StartupInfo) };
        Win32.ProcessInformation pi;
        fixed (char* c = cmd)
        {
            if (Win32.CreateProcessW(exe, c, 0, 0, 0, Win32.DEBUG_ONLY_THIS_PROCESS, 0, null, &si, &pi) == 0)
            {
                Log.Info($"real taskmgr: CreateProcess failed ({System.Runtime.InteropServices.Marshal.GetLastPInvokeError()})");
                return false;
            }
        }
        Win32.DebugSetProcessKillOnExit(0);
        Win32.DebugActiveProcessStop(pi.ProcessId);
        Kernel32.CloseHandle(pi.Thread);
        Kernel32.CloseHandle(pi.Process);
        return true;
    }

    /// <summary>
    /// Turns the takeover on or off from a window without admin: an elevated copy of this exe does the registry
    /// part (one UAC prompt) and saves the result. Returns an error message or null.
    /// </summary>
    public static string? SetReplaceElevated(Settings s, bool on)
    {
        if (Elevation.IsElevated) return SetReplace(s, on);
        try
        {
            var psi = new ProcessStartInfo(ExePath) { UseShellExecute = true, Verb = "runas" };
            psi.ArgumentList.Add("--set-replace");
            psi.ArgumentList.Add(on ? "on" : "off");
            using var p = Process.Start(psi)!;
            p.WaitForExit(15000);
            if (!p.HasExited || p.ExitCode != 0) return "Couldn't change it. Details are in the log.";
            // The helper saved the bookkeeping; take it so our next save doesn't undo it.
            var saved = Settings.Load();
            s.PreviousDebugger = saved.PreviousDebugger;
            s.ReplacedWith = saved.ReplacedWith;
            return null;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "Windows needs admin permission to change this, and the prompt was cancelled.";
        }
        catch (Exception ex)
        {
            Log.Error("set replace", ex);
            return ex.Message;
        }
    }

    /// <summary>
    /// Applies the setting at startup. Repoints a takeover of ours whose path went stale (the app moved), since a
    /// dead path would make Ctrl+Shift+Esc open nothing. If something else replaced ours since, that choice wins
    /// and the setting turns off.
    /// </summary>
    public static void Reconcile(Settings s)
    {
        if (!Elevation.IsElevated || !s.ReplaceTaskManager || IsReplacing()) return;
        string? cur = CurrentDebugger();
        string? curExe = cur?.Trim().Trim('"');
        bool deadPath = curExe != null && !File.Exists(curExe);
        if (s.ReplacedWith != null && cur != null && cur != s.ReplacedWith && !deadPath)
        {
            Log.Info($"ifeo taken over by '{cur}', leaving it");
            s.ReplaceTaskManager = false;
            s.ReplacedWith = null;
            s.Save();
            return;
        }
        SetReplace(s, true);
    }

    // ---------- start with Windows ----------

    private const string StartValue = "TaskMaster";

    /// <summary>Writes or removes our own Run value; --tray starts it in the notification area without a UAC prompt.</summary>
    public static string? SetStartWithWindows(bool on)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (on) run.SetValue(StartValue, $"\"{ExePath}\" --tray", RegistryValueKind.String);
            else run.DeleteValue(StartValue, throwOnMissingValue: false);
            return null;
        }
        catch (Exception ex) { Log.Error("start with windows", ex); return ex.Message; }
    }

    // ---------- custom shortcut ----------

    public static void ApplyCustom(Settings s)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            // Ctrl+Shift+Esc on any sides can't be registered; Windows keeps it (see Open with Ctrl+Shift+Esc).
            if (s.CustomShortcut.Count == 0 || Shortcuts.IsSystemCombo(s.CustomShortcut))
            {
                run.DeleteValue(RunValue, throwOnMissingValue: false);
                SignalAgent(AgentExitName);
                return;
            }
            run.SetValue(RunValue, $"\"{ExePath}\" --agent", RegistryValueKind.String);
            // A running agent re-reads the settings; otherwise start one now.
            if (!SignalAgent(AgentReloadName))
                Process.Start(new ProcessStartInfo(ExePath, "--agent") { UseShellExecute = false });
        }
        catch (Exception ex) { Log.Error("custom shortcut", ex); }
    }

    private static bool SignalAgent(string name)
    {
        nint h = Win32.OpenEventW(Win32.EVENT_MODIFY_STATE, 0, name);
        if (h == 0) return false;
        Win32.SetEvent(h);
        Kernel32.CloseHandle(h);
        return true;
    }

    // ---------- key names ----------

    public const int VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3,
        VK_LMENU = 0xA4, VK_RMENU = 0xA5, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    public static readonly int[] SideModifiers = [VK_LCONTROL, VK_RCONTROL, VK_LSHIFT, VK_RSHIFT, VK_LMENU, VK_RMENU, VK_LWIN, VK_RWIN];

    public static bool IsModifier(int vk) => SideModifiers.Contains(vk) || vk is 0x10 or 0x11 or 0x12;

    public static uint ModifierFlag(int vk) => vk switch
    {
        VK_LCONTROL or VK_RCONTROL => Win32.MOD_CONTROL,
        VK_LSHIFT or VK_RSHIFT => Win32.MOD_SHIFT,
        VK_LMENU or VK_RMENU => Win32.MOD_ALT,
        VK_LWIN or VK_RWIN => Win32.MOD_WIN,
        _ => 0,
    };

    public static string Describe(IReadOnlyList<int> keys) =>
        keys.Count == 0 ? "None" : string.Join(" + ", keys.Select(KeyName));

    public static string KeyName(int vk) => vk switch
    {
        VK_LCONTROL => "Left Ctrl", VK_RCONTROL => "Right Ctrl",
        VK_LSHIFT => "Left Shift", VK_RSHIFT => "Right Shift",
        VK_LMENU => "Left Alt", VK_RMENU => "Right Alt",
        VK_LWIN => "Left Win", VK_RWIN => "Right Win",
        0x1B => "Esc", 0x20 => "Space", 0x0D => "Enter", 0x09 => "Tab", 0x08 => "Backspace",
        0x2E => "Delete", 0x2D => "Insert", 0x24 => "Home", 0x23 => "End", 0x21 => "Page Up", 0x22 => "Page Down",
        0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down", 0x13 => "Pause", 0x91 => "Scroll Lock",
        0x2C => "Print Screen",
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => $"Num {vk - 0x60}",
        _ => $"Key {vk:X2}",
    };

    /// <summary>True when the custom shortcut is just Ctrl+Shift+Esc on some sides, which Windows keeps for itself.</summary>
    public static bool IsSystemCombo(IReadOnlyList<int> keys) =>
        keys.Count == 3 && keys.Contains(0x1B)
        && keys.Any(k => k is VK_LCONTROL or VK_RCONTROL) && keys.Any(k => k is VK_LSHIFT or VK_RSHIFT);
}

public static class Elevation
{
    public static bool IsElevated { get; } = Environment.IsPrivilegedProcess;

    /// <summary>Starts an elevated copy (UAC prompt). False if the user said no, so we carry on without admin.</summary>
    public static bool Relaunch(IEnumerable<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(Shortcuts.ExePath) { UseShellExecute = true, Verb = "runas" };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi);
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false; // UAC cancelled
        }
        catch (Exception ex)
        {
            Log.Error("elevate", ex);
            return false;
        }
    }
}
