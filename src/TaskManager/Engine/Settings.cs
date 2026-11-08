using System.Text.Json;

namespace TaskManager.Engine;

public enum UpdateSpeed { High, Normal, Low, Paused }
public enum AppTheme { System, Light, Dark }
/// <summary>The window material: Task Manager's Mica, or see-through Acrylic / Glass (Glass's tint is adjustable).</summary>
public enum Backdrop { Mica, MicaAlt, Acrylic, Glass }

public sealed class ColumnPref
{
    public string Id { get; set; } = "";
    public double Width { get; set; }
    public bool Visible { get; set; }
}

/// <summary>User settings, saved as JSON in %LOCALAPPDATA%\TaskManager. A missing or broken file means defaults.</summary>
public sealed class Settings
{
    public string StartPage { get; set; } = "Processes";
    public UpdateSpeed Speed { get; set; } = UpdateSpeed.Normal;
    public AppTheme Theme { get; set; } = AppTheme.System;
    public bool AlwaysOnTop { get; set; }
    public Backdrop Backdrop { get; set; } = Backdrop.Mica;
    /// <summary>Glass tint, 0 (clear) to 100 (nearly solid).</summary>
    public int GlassTint { get; set; } = 25;

    /// <summary>Size of everything inside the window (text, graphs, controls), in percent of normal.</summary>
    public int UiScale { get; set; } = 100;
    public const int MinScale = 50, MaxScale = 150;
    public double Scale => Math.Clamp(UiScale, MinScale, MaxScale) / 100.0;

    /// <summary>Last window bounds in physical pixels (null until the window has been closed once), and whether it was maximized.</summary>
    public int[]? WindowBounds { get; set; }
    public bool WindowMaximized { get; set; }

    /// <summary>Font family for the window's text. Empty = the Windows default.</summary>
    public string Font { get; set; } = "";

    /// <summary>Release notes left by the previous version for the one it installed, shown once ("What's new").</summary>
    public string? PendingNotesVersion { get; set; }
    public string? PendingNotes { get; set; }

    /// <summary>A version that failed to start after installing and was rolled back; not offered again.</summary>
    public string? FailedUpdateVersion { get; set; }

    /// <summary>Closing the window keeps TaskMaster running in the notification area (tray icon: Open / Quit).</summary>
    public bool CloseToTray { get; set; }

    /// <summary>Starts quietly in the tray when Windows signs in (HKCU Run value, see Shortcuts.SetStartWithWindows).</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>Settings this version doesn't know (written by a newer one), kept so a downgrade or rollback doesn't drop them.</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    /// <summary>Anything that starts taskmgr.exe (Ctrl+Alt+Del, taskbar, Start, Run) opens this app instead.</summary>
    public bool ReplaceTaskManager { get; set; } = true;

    /// <summary>With the takeover on, Ctrl+Shift+Esc opens this app too; off, it opens Windows Task Manager.</summary>
    public bool ReplaceCtrlShiftEsc { get; set; } = true;

    /// <summary>Whatever Debugger value taskmgr.exe had before we took it over, restored when turned off.</summary>
    public string? PreviousDebugger { get; set; }

    /// <summary>The Debugger value we wrote, to tell our takeover from another tool's.</summary>
    public string? ReplacedWith { get; set; }

    /// <summary>Extra shortcut as side-specific virtual keys (e.g. Right Ctrl, Right Shift, Esc). Empty = none.</summary>
    public List<int> CustomShortcut { get; set; } = new();

    /// <summary>Processes-page columns in display order, with width and visibility. Null = Task Manager's defaults.</summary>
    public List<ColumnPref>? ProcessColumns { get; set; }

    /// <summary>Resource values › Memory › Percents.</summary>
    public bool MemoryAsPercent { get; set; }

    public bool GroupByType { get; set; } = true;

    /// <summary>Column order/width/visibility for the other pages' tables, by table name.</summary>
    public Dictionary<string, List<ColumnPref>> Tables { get; set; } = new();

    /// <summary>Performance page: last selected device and CPU graph mode.</summary>
    public string PerfDevice { get; set; } = "CPU";
    public bool CpuLogicalGraphs { get; set; }

    /// <summary>Seconds between updates (any value, e.g. 2, 1.5, 0.5). Null in files saved before it existed: taken from Speed.</summary>
    public double? UpdateSeconds { get; set; }
    public bool UpdatePaused { get; set; }

    // 0.25 s is the sampler's floor (Sampler.MinGapMs): faster would cost more CPU than the numbers are worth.
    public const double MinSeconds = 0.25, MaxSeconds = 2, StepSeconds = 0.05;

    public double Seconds => Math.Clamp(UpdateSeconds ?? Speed switch { UpdateSpeed.High => 0.5, UpdateSpeed.Low => 4, _ => 1 }, MinSeconds, MaxSeconds);
    public bool IsPaused => UpdateSeconds == null ? Speed == UpdateSpeed.Paused : UpdatePaused;
    public int IntervalMs => (int)Math.Round(Seconds * 1000);

    // TASKMASTER_DATA gives test runs their own folder so they never touch the user's settings.
    public static string Folder { get; } = Environment.GetEnvironmentVariable("TASKMASTER_DATA") ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskManager");
    private static string FilePath => Path.Combine(Folder, "settings.json");
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), s_json) ?? new();
        }
        catch (Exception ex) { Log.Error("settings load", ex); }
        return new();
    }

    /// <summary>Test runs (--snapshot) change settings in memory only, never the user's file.</summary>
    public static bool ReadOnly { get; set; }

    public void Save()
    {
        if (ReadOnly) return;
        try
        {
            Directory.CreateDirectory(Folder);
            // Write-then-replace so a crash mid-save never leaves a half-written file.
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, s_json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) { Log.Error("settings save", ex); }
    }
}
