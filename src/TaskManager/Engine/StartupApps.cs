using System.Diagnostics;
using Microsoft.Win32;

namespace TaskManager.Engine;

public sealed record StartupEntry(
    string Key, string Name, string Publisher, string Path, string Command, string Source, bool Enabled,
    RegistryHive ApprovedHive, string ApprovedKey, string ApprovedValue, DateTime? DisabledAt);

/// <summary>
/// Startup apps from the Run keys and Startup folders, enabled or disabled through the same StartupApproved values
/// Task Manager writes (first byte even = enabled, odd = disabled; disabling also stamps the time).
/// </summary>
public static class StartupApps
{
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static List<StartupEntry> Query()
    {
        var list = new List<StartupEntry>();
        void FromRun(RegistryKey root, string runKey, string approvedName, string source)
        {
            try
            {
                using var run = root.OpenSubKey(runKey);
                if (run == null) return;
                foreach (var name in run.GetValueNames())
                {
                    if (name.Length == 0 || run.GetValue(name) is not string cmd || cmd.Length == 0) continue;
                    string exe = ExeOf(cmd);
                    var (enabled, at) = ReadApproved(root, Approved + approvedName, name);
                    var (title, publisher) = Describe(exe, name);
                    list.Add(new StartupEntry($"{source}|{name}", title, publisher, exe, cmd, source, enabled,
                        root == Registry.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
                        Approved + approvedName, name, at));
                }
            }
            catch (Exception ex) { Log.Error("startup " + source, ex); }
        }

        FromRun(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run", "Registry (user)");
        FromRun(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Run", "Registry (machine)");
        FromRun(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32", "Registry (machine, 32-bit)");

        void FromFolder(string folder, RegistryKey approvedRoot, string source)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    string fileName = System.IO.Path.GetFileName(file);
                    if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    string target = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutTarget(file) : file;
                    var (enabled, at) = ReadApproved(approvedRoot, Approved + "StartupFolder", fileName);
                    var (title, publisher) = Describe(target, System.IO.Path.GetFileNameWithoutExtension(file));
                    list.Add(new StartupEntry($"{source}|{fileName}", title, publisher, target.Length > 0 ? target : file, file, source, enabled,
                        approvedRoot == Registry.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
                        Approved + "StartupFolder", fileName, at));
                }
            }
            catch (Exception ex) { Log.Error("startup folder", ex); }
        }

        FromFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), Registry.CurrentUser, "Startup folder (user)");
        FromFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Registry.LocalMachine, "Startup folder (all users)");
        return list;
    }

    /// <summary>Writes the StartupApproved value. Returns an error message, or null on success.</summary>
    public static string? SetEnabled(StartupEntry e, bool enabled)
    {
        try
        {
            var root = e.ApprovedHive == RegistryHive.LocalMachine ? Registry.LocalMachine : Registry.CurrentUser;
            using var key = root.CreateSubKey(e.ApprovedKey, writable: true);
            var data = new byte[12];
            data[0] = enabled ? (byte)2 : (byte)3;
            if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
            key.SetValue(e.ApprovedValue, data, RegistryValueKind.Binary);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return "Changing apps that start for all users needs TaskMaster to run as administrator.";
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Time the firmware took before Windows started loading, in seconds (0 = not reported).</summary>
    public static double LastBiosSeconds()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power");
            return k?.GetValue("FwPOSTTime") is int ms ? ms / 1000.0 : 0;
        }
        catch (Exception) { return 0; }
    }

    private static (bool enabled, DateTime? at) ReadApproved(RegistryKey root, string key, string value)
    {
        try
        {
            using var k = root.OpenSubKey(key);
            if (k?.GetValue(value) is byte[] { Length: >= 1 } b)
            {
                bool on = (b[0] & 1) == 0;
                DateTime? at = !on && b.Length >= 12 && BitConverter.ToInt64(b, 4) is long ft and > 0 ? DateTime.FromFileTimeUtc(ft).ToLocalTime() : null;
                return (on, at);
            }
        }
        catch (Exception) { /* unreadable = treat as enabled, like Task Manager */ }
        return (true, null);
    }

    private static (string, string) Describe(string exe, string fallback)
    {
        if (exe.Length == 0 || !File.Exists(exe)) return (fallback, "");
        try
        {
            var v = FileVersionInfo.GetVersionInfo(exe);
            string desc = v.FileDescription?.Trim() ?? "";
            return (desc.Length > 0 ? desc : fallback, v.CompanyName?.Trim() ?? "");
        }
        catch (Exception) { return (fallback, ""); }
    }

    /// <summary>The program part of a Run command line ("C:\x\a.exe" /min → C:\x\a.exe), env vars expanded.</summary>
    public static string ExeOf(string cmd)
    {
        cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            return end > 1 ? cmd[1..end] : cmd.Trim('"');
        }
        int exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return cmd[..(exe + 4)];
        int sp = cmd.IndexOf(' ');
        string first = sp > 0 ? cmd[..sp] : cmd;
        if (!first.Contains('\\'))
        {
            string sys = System.IO.Path.Combine(Environment.SystemDirectory, first.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? first : first + ".exe");
            if (File.Exists(sys)) return sys;
        }
        return first;
    }

    private static string ShortcutTarget(string lnk)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return "";
            dynamic shell = Activator.CreateInstance(t)!;
            try
            {
                dynamic sc = shell.CreateShortcut(lnk);
                return (string)sc.TargetPath ?? "";
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }
        catch (Exception) { return ""; }
    }
}
