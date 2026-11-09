using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using TaskManager.Engine;
using TaskManager.Views;

namespace TaskManager;

public partial class App : Application
{
    private Window? _window;

    public static Sampler Sampler { get; } = new();
    public static bool Closing { get; private set; }
    public static IconCache Icons { get; private set; } = null!;
    public static Snapshot? LastFrame { get; private set; }
    public static Settings Settings { get; } = Settings.Load();
    public static MainWindow? MainWindow { get; private set; }

    /// <summary>The page to come back on after an update relaunch (--after-update &lt;page&gt;), else null.</summary>
    public static string? ResumePage { get; } = Environment.GetCommandLineArgs() is var a
        && Array.IndexOf(a, "--after-update") is var i && i >= 0 && i + 1 < a.Length ? a[i + 1] : null;

    /// <summary>Started with Windows: stays in the tray until opened.</summary>
    public static bool StartInTray { get; } = Environment.GetCommandLineArgs().Contains("--tray");

    /// <summary>The last run never reached a normal exit (see the "running" marker); the window says so once.</summary>
    public static bool CrashedLastTime { get; set; }

    private static string RunningMarker => Path.Combine(TaskManager.Engine.Settings.Folder, "running");

    // Overwritten, never deleted: "running" while up, "closed" after a normal exit.
    private static void MarkRunning(bool running)
    {
        try
        {
            if (running) CrashedLastTime = File.Exists(RunningMarker) && File.ReadAllText(RunningMarker) == "running";
            Directory.CreateDirectory(TaskManager.Engine.Settings.Folder);
            File.WriteAllText(RunningMarker, running ? "running" : "closed");
        }
        catch (Exception ex) { Log.Error("running marker", ex); }
    }

    /// <summary>
    /// In the tray with the window hidden: no sampling, no kernel trace, efficiency mode (EcoQoS + idle priority,
    /// like Task Manager's), and the working set handed back to Windows. Opening the window undoes it all.
    /// </summary>
    public static void SetDormant(bool on)
    {
        Sampler.Paused = on;
        uint pid = (uint)Environment.ProcessId;
        ProcessActions.SetEfficiency(pid, on);
        if (on)
        {
            EtwIo.Stop();
            Trim();
            // Again once start-up or the last frame has settled, which allocates after this call.
            Task.Delay(5000).ContinueWith(_ => { if (Sampler.Paused) Trim(); });
        }
        else if (Sampler.UseEtw && Elevation.IsElevated)
            Task.Run(EtwIo.Start); // a few ms of setup; keep it off the UI thread
    }

    private static void Trim()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, -1, -1);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(nint process, nint min, nint max);

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            // e.Message carries WinUI's detail (XAML line and position) that the exception itself drops.
            Log.Error("ui: " + e.Message, e.Exception);
            // Keep running for anything a page can recover from; the error is in the log.
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("domain", (Exception)e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("task", e.Exception); e.SetObserved(); };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var ui = DispatcherQueue.GetForCurrentThread();
        if (SnapshotMode.Parse() != null)
        {
            TaskManager.Engine.Settings.ReadOnly = true;
            // Snapshot runs can pick the Performance device: --device Memory / Disk0 / GPU0.
            var a = Environment.GetCommandLineArgs();
            int d = Array.IndexOf(a, "--device");
            if (d >= 0 && d + 1 < a.Length) Settings.PerfDevice = a[d + 1];
            int b = Array.IndexOf(a, "--backdrop");
            if (b >= 0 && b + 1 < a.Length && Enum.TryParse<TaskManager.Engine.Backdrop>(a[b + 1], out var bd)) Settings.Backdrop = bd;
            int sc = Array.IndexOf(a, "--scale");
            if (sc >= 0 && sc + 1 < a.Length && int.TryParse(a[sc + 1], out int pct)) Settings.UiScale = pct;
            int fo = Array.IndexOf(a, "--font");
            if (fo >= 0 && fo + 1 < a.Length) Settings.Font = a[fo + 1];
        }
        Icons = new IconCache(ui);
        Sampler.IntervalMs = Settings.IntervalMs;
        Sampler.Frozen = Settings.IsPaused;
        bool snapshotRun = SnapshotMode.Parse() != null;
        if (!snapshotRun) MarkRunning(true);
        var main = new MainWindow();
        _window = main;
        MainWindow = main;
        if (StartInTray && !snapshotRun) SetDormant(true); // hidden until opened from the tray
        else _window.Activate();

        // On close: stop sampling first so no frame reaches XAML that is being torn down.
        _window.Closed += (_, _) => { Closing = true; Sampler.Dispose(); AppHistory.Save(); if (!snapshotRun) MarkRunning(false); };
        // Data starts only after the window is up, so the first frame is never held back.
        Sampler.FrameReady += f => ui.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            if (Closing) return;
            LastFrame = f;
            PerfHistory.Add(f);
            AppHistory.Add(f);
            ProcessesPage.Current?.OnFrame(f);
            MainWindow?.FramePage?.OnFrame(f);
        });
        Sampler.Start();

        if (SnapshotMode.Parse() is { } snap) { _ = SnapshotMode.RunAsync(_window, snap.path, snap.delay); return; }

        SingleInstance.Listen(toggle => ui.TryEnqueue(() => main.OnActivationSignal(toggle)));
        if (ResumePage != null) Updater.ConfirmStarted();
        Updater.Start();
        Shortcuts.Reconcile(Settings);
        if (Settings.CustomShortcut.Count > 0) Shortcuts.ApplyCustom(Settings);
    }
}
