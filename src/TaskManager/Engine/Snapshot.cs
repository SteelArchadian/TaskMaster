namespace TaskManager.Engine;

public enum ProcessCategory { Apps, Background, Windows }

/// <summary>One child line under a group: a process, or a window title for single-process apps.</summary>
public sealed record ChildData(
    string Key, string Name, string IconPath, uint Pid, double Cpu, long Memory, double Disk, double Gpu, string GpuEngine, bool IsWindow, double? Network = null);

/// <summary>One top-level row on the Processes page, already aggregated off the UI thread.</summary>
public sealed record GroupData(
    string Key,
    ProcessCategory Category,
    string Name,
    string IconPath,
    string Publisher,
    uint Pid,
    string ImageName,
    double Cpu,
    long Memory,
    double Disk,
    double Gpu,
    string GpuEngine,
    int ProcessCount,
    IReadOnlyList<ChildData> Children,
    IReadOnlyList<uint> Pids,
    nint MainWindow,
    double? Network = null,
    bool Suspended = false);

/// <summary>One line on the Details page. Disk in bytes/s; Network in bits/s, null when it can't be measured.</summary>
public sealed record ProcessDetail(ProcessSample S, string Path, string Description, string User, string Arch, double Disk, double? Network, double Gpu);

/// <summary>GpuTotal is null when no GPU could be read, so the column stays blank instead of showing 0%.</summary>
public sealed record Snapshot(SystemSample System, double? GpuTotal, IReadOnlyList<GroupData> Groups, IReadOnlyList<ProcessSample> Processes)
{
    public IReadOnlyList<ProcessDetail> Details { get; init; } = [];
    public PerfSample? Perf { get; init; }
    public IReadOnlyList<ServiceEntry> Services { get; init; } = [];
    /// <summary>Utilization per GPU adapter, keyed by <see cref="GpuInfo.Luid"/>.</summary>
    public IReadOnlyDictionary<string, double> GpuByAdapter { get; init; } = new Dictionary<string, double>();
    /// <summary>Utilization per engine type ("3D", "Copy", "Video Decode"…) per adapter, in the driver's node order.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<(string Name, double Percent)>> GpuEngines { get; init; } = new Dictionary<string, IReadOnlyList<(string, double)>>();
    /// <summary>GPU temperature in °C per adapter, where the driver reports one.</summary>
    public IReadOnlyDictionary<string, double> GpuTemps { get; init; } = new Dictionary<string, double>();
    /// <summary>Busiest disk's active time, and network use as a share of link speed (header totals).</summary>
    public double DiskPercent { get; init; }
    public double? NetworkPercent { get; init; }
    /// <summary>Per-process disk and network come from the kernel trace (admin); otherwise disk is an estimate.</summary>
    public bool IoTraced { get; init; }
}
