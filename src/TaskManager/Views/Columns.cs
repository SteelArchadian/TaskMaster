using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace TaskManager.Views;

public enum ColumnId { Name, Status, Publisher, Pid, ProcessName, Type, CommandLine, Cpu, Memory, Disk, Network, Gpu, GpuEngine, Power, PowerTrend }

/// <summary>A Processes-page column. Widths match Task Manager at 100% scaling.</summary>
public sealed class ColumnDef
{
    public required ColumnId Id { get; init; }
    public required string Header { get; init; }
    public double Width { get; set; }
    public bool IsHeat { get; init; }
    public bool RightAlign { get; init; }
    public bool Visible { get; set; }

    public static List<ColumnDef> Defaults() =>
    [
        new() { Id = ColumnId.Name, Header = "Name", Width = 248, Visible = true },
        new() { Id = ColumnId.Status, Header = "Status", Width = 96, Visible = true },
        new() { Id = ColumnId.Publisher, Header = "Publisher", Width = 140 },
        new() { Id = ColumnId.Pid, Header = "PID", Width = 73, RightAlign = true },
        new() { Id = ColumnId.ProcessName, Header = "Process name", Width = 140 },
        new() { Id = ColumnId.Type, Header = "Type", Width = 120 },
        new() { Id = ColumnId.CommandLine, Header = "Command line", Width = 300 },
        new() { Id = ColumnId.Cpu, Header = "CPU", Width = 73, IsHeat = true, RightAlign = true, Visible = true },
        new() { Id = ColumnId.Memory, Header = "Memory", Width = 90, IsHeat = true, RightAlign = true, Visible = true },
        new() { Id = ColumnId.Disk, Header = "Disk", Width = 73, IsHeat = true, RightAlign = true, Visible = true },
        new() { Id = ColumnId.Network, Header = "Network", Width = 73, IsHeat = true, RightAlign = true, Visible = true },
        new() { Id = ColumnId.Gpu, Header = "GPU", Width = 73, IsHeat = true, RightAlign = true, Visible = true },
        new() { Id = ColumnId.GpuEngine, Header = "GPU engine", Width = 73, Visible = true },
        new() { Id = ColumnId.Power, Header = "Power usage", Width = 90, IsHeat = true, Visible = true },
        new() { Id = ColumnId.PowerTrend, Header = "Power usage trend", Width = 90, IsHeat = true, Visible = true },
    ];

    public const double MinWidth = 40;

    /// <summary>Defaults reordered and resized by the saved preferences. Unknown or missing columns keep their defaults.</summary>
    public static List<ColumnDef> Load(List<TaskManager.Engine.ColumnPref>? prefs)
    {
        var defs = Defaults();
        if (prefs == null || prefs.Count == 0) return defs;
        var result = new List<ColumnDef>();
        foreach (var p in prefs)
        {
            var d = defs.FirstOrDefault(c => c.Id.ToString() == p.Id);
            if (d == null || result.Contains(d)) continue;
            d.Width = Math.Max(MinWidth, p.Width);
            d.Visible = p.Visible || d.Id == ColumnId.Name;
            result.Add(d);
        }
        foreach (var d in defs) if (!result.Contains(d)) result.Add(d);
        // Name always leads, as in Task Manager.
        var name = result.First(c => c.Id == ColumnId.Name);
        result.Remove(name);
        result.Insert(0, name);
        return result;
    }

    public static List<TaskManager.Engine.ColumnPref> Save(IEnumerable<ColumnDef> cols) =>
        cols.Select(c => new TaskManager.Engine.ColumnPref { Id = c.Id.ToString(), Width = c.Width, Visible = c.Visible }).ToList();
}

/// <summary>Task Manager's number formats and heat-map colors.</summary>
public static class Fmt
{
    private static readonly CultureInfo C = CultureInfo.CurrentCulture;

    public static string Percent(double v) => v < 0.05 ? "0%" : v.ToString("0.0", C) + "%";

    public static string MemoryPercent(double pct) => pct.ToString("0.0", C) + "%";

    public static string Megabytes(long bytes) => (bytes / 1048576.0).ToString("N1", C) + " MB";

    public static string DiskRate(double bytesPerSec)
    {
        double mb = bytesPerSec / 1048576.0;
        return mb < 0.05 ? "0 MB/s" : mb.ToString("0.0", C) + " MB/s";
    }

    public static string NetRate(double bitsPerSec)
    {
        double mbps = bitsPerSec / 1_000_000.0;
        return mbps < 0.05 ? "0 Mbps" : mbps.ToString("0.0", C) + " Mbps";
    }

    // Dark-theme heat levels sampled from Task Manager on Windows 11 (100% scaling).
    private static readonly SolidColorBrush[] s_dark =
    [
        new(Color.FromArgb(255, 0x1B, 0x26, 0x35)),
        new(Color.FromArgb(255, 0x1D, 0x35, 0x59)),
        new(Color.FromArgb(255, 0x24, 0x47, 0x7A)),
        new(Color.FromArgb(255, 0x2E, 0x5C, 0x9E)),
        new(Color.FromArgb(255, 0x3A, 0x73, 0xC4)),
    ];

    private static readonly SolidColorBrush[] s_light =
    [
        new(Color.FromArgb(255, 0xFF, 0xF4, 0xC4)),
        new(Color.FromArgb(255, 0xFF, 0xE8, 0x9C)),
        new(Color.FromArgb(255, 0xFF, 0xD2, 0x66)),
        new(Color.FromArgb(255, 0xFF, 0xB9, 0x47)),
        new(Color.FromArgb(255, 0xFF, 0x9C, 0x2A)),
    ];

    public static SolidColorBrush HeatSeparatorDark { get; } = new(Color.FromArgb(255, 0x30, 0x3D, 0x52));
    public static SolidColorBrush HeatSeparatorLight { get; } = new(Color.FromArgb(255, 0xF2, 0xE2, 0xA0));

    /// <summary>
    /// Over Acrylic or Glass the solid heat shades would read as opaque slabs, so they turn see-through: the idle shade
    /// mostly, busier shades less so they still stand out. The brushes are shared, so every cell follows at once.
    /// </summary>
    public static void SetSeeThrough(bool on)
    {
        foreach (var set in new[] { s_dark, s_light })
            for (int i = 0; i < set.Length; i++) set[i].Opacity = on ? (i == 0 ? 0.3 : 0.75) : 1;
        HeatSeparatorDark.Opacity = HeatSeparatorLight.Opacity = on ? 0.5 : 1;
    }

    /// <summary>Heat level 0..4. <paramref name="lowCut"/> is where Task Manager leaves the base shade.</summary>
    public static int Level(double percentOfMax, double lowCut)
    {
        if (percentOfMax < lowCut) return 0;
        if (percentOfMax < 50) return 1;
        if (percentOfMax < 75) return 2;
        if (percentOfMax < 90) return 3;
        return 4;
    }

    public static SolidColorBrush Heat(int level, ElementTheme theme) =>
        (theme == ElementTheme.Light ? s_light : s_dark)[level];

    public static SolidColorBrush Transparent { get; } = new(Colors.Transparent);
}
