using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using TaskManager.Engine;

namespace TaskManager;

/// <summary>
/// Startup decisions happen before any XAML loads, so a second launch or the agent costs almost nothing:
/// agent mode, hand off to a running window, ask for admin (UAC) like Task Manager, then start the UI.
/// When Task Manager is taken over, Windows passes taskmgr.exe's own path and its switch (/2 = Ctrl+Shift+Esc) first.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--agent")) return HotkeyAgent.Run();

        // The new version installing itself over the app folder once the old one has closed (see Updater).
        if (args.Length >= 4 && args[0] == "--apply-update" && int.TryParse(args[2], out int oldPid))
            return Updater.Apply(args[1], oldPid, args[3]);

        // Test hook: TASKMASTER_TEST_FAIL_START=<version> makes that version fail to come up after an update, to exercise the rollback.
        if (args.Contains("--after-update") && Environment.GetEnvironmentVariable("TASKMASTER_TEST_FAIL_START") == Updater.Current.ToString()) return 3;

        // Elevated helper for the "Replace Task Manager" setting (see Shortcuts.SetReplaceElevated).
        int sr = Array.IndexOf(args, "--set-replace");
        if (sr >= 0 && sr + 1 < args.Length)
        {
            var s = Settings.Load();
            bool on = args[sr + 1] == "on";
            if (Shortcuts.SetReplace(s, on) != null) return 1;
            s.ReplaceTaskManager = on;
            s.Save();
            return 0;
        }

        // Ctrl+Shift+Esc left to Windows while the rest is taken over: hand this launch to the real Task Manager.
        if (Shortcuts.IsCtrlShiftEscLaunch(args) && !Settings.Load().ReplaceCtrlShiftEsc
            && Shortcuts.StartRealTaskManager(string.Join(' ', args.Skip(1))))
            return 0;

        bool snapshot = args.Contains("--snapshot");
        if (!snapshot)
        {
            Log.Info("launch: " + Environment.CommandLine);
            if (SingleInstance.Signal(toggle: false)) return 0;
            // A declined UAC prompt (or --no-elevate) carries on without admin; some columns stay blank then.
            // Started with Windows (--tray) skips it too: a UAC prompt at sign-in is the opposite of quiet.
            if (!Elevation.IsElevated && !args.Contains("--no-elevate") && !args.Contains("--elevated") && !args.Contains("--tray")
                && Elevation.Relaunch(args.Where(a => !a.EndsWith("taskmgr.exe", StringComparison.OrdinalIgnoreCase)).Append("--elevated")))
                return 0;
            if (!SingleInstance.Claim()) { SingleInstance.Signal(toggle: false); return 0; }
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }
}
