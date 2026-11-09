using TaskManager.Native;

namespace TaskManager.Engine;

/// <summary>
/// The only place data is gathered. Runs on its own below-normal thread so the window never waits
/// for it, pauses while the window is hidden, and survives a failing source by logging and moving on.
/// </summary>
public sealed class Sampler : IDisposable
{
    private readonly ProcessSampler _processes = new();
    private readonly SystemSampler _system = new();
    private readonly ImageInfoCache _images = new();
    private readonly ProcessOwner _owners = new();
    private readonly PerfSampler _perf = new();
    private List<ServiceEntry> _services = new();
    private long _ioStamp;
    private GpuSampler? _gpu;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _stop;
    private volatile bool _paused;
    private volatile bool _frozen;
    private int _tick;
    private Dictionary<uint, List<ServiceEntry>> _servicesByPid = new();
    private static readonly string s_windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\";

    // Fixed system processes Task Manager lists under "Windows processes" even without a path.
    private static readonly HashSet<string> s_kernelNames = new(StringComparer.OrdinalIgnoreCase)
        { "System", "Registry", "Memory Compression", "Secure System" };

    public event Action<Snapshot>? FrameReady;

    /// <summary>Milliseconds between samples. Task Manager's "Normal" speed is 1000.</summary>
    public int IntervalMs { get; set; } = 1000;
    private const int MinGapMs = 250;

    public bool Paused
    {
        get => _paused;
        // Wake only on the paused -> running edge: the window reports state on every resize step, and a wake per
        // step meant samples a few ms apart, whose CPU/disk/GPU deltas are mostly timer noise.
        set { bool was = _paused; _paused = value; if (was && !value) _wake.Set(); }
    }

    /// <summary>"Update speed: Paused" in Settings. Separate from Paused, which follows the window state.</summary>
    public bool Frozen
    {
        get => _frozen;
        set { bool was = _frozen; _frozen = value; if (was && !value) _wake.Set(); }
    }

    public static bool UseEtw { get; } = !Environment.GetCommandLineArgs().Contains("--no-etw");

    public Sampler()
    {
        _thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Sampler" };
    }

    public void Start() => _thread.Start();

    /// <summary>Takes a sample now instead of waiting for the next tick (F5 / after an action).</summary>
    public void RefreshNow() => _wake.Set();

    /// <summary>After a service command: re-read the service list on the next sample instead of within 5 ticks.</summary>
    public void RefreshServices() { _forceServices = true; _wake.Set(); }
    private volatile bool _forceServices;

    private void Run()
    {
        // Created here, not in the constructor, so its one-time engine-name lookup never touches the UI thread.
        try { _gpu = new GpuSampler(); }
        catch (Exception ex) { Log.Error("gpu init", ex); }
        // Started dormant (tray), the kernel trace waits until the window opens (App.SetDormant).
        if (!_paused && UseEtw) EtwIo.Start();

        // Prime the CPU deltas so the first frame shown already has real percentages.
        TrySample(publish: false);
        Thread.Sleep(250);
        while (!_stop)
        {
            bool idle = _paused || _frozen;
            long sampledAt = Environment.TickCount64;
            if (!idle) TrySample(publish: true);
            _wake.WaitOne(idle ? Timeout.Infinite : IntervalMs);
            // Rates are deltas over the gap since the last sample; never let an early wake make that gap tiny.
            long gap = Environment.TickCount64 - sampledAt;
            if (gap < MinGapMs) Thread.Sleep((int)(MinGapMs - gap));
        }
    }

    private void TrySample(bool publish)
    {
        try
        {
            var frame = Build();
            if (publish) FrameReady?.Invoke(frame);
        }
        catch (Exception ex)
        {
            Log.Error("sampler", ex);
        }
    }

    private Snapshot Build()
    {
        if (_tick++ % 5 == 0 || _forceServices)
        {
            _forceServices = false;
            try
            {
                _services = ServiceMap.Query();
                foreach (var svc in _services) ServiceControl.GroupOf(svc.Name); // cached; first pass warms it off the UI thread
                _servicesByPid = _services.Where(s => s.Pid != 0)
                    .GroupBy(s => s.Pid).ToDictionary(g => g.Key, g => g.ToList());
            }
            catch (Exception ex) { Log.Error("services", ex); }
        }

        var sys = _system.Sample();
        var procs = _processes.Sample();
        var windows = WindowScanner.Scan();
        Dictionary<uint, GpuUse> gpu;
        try { gpu = _gpu?.Sample(procs) ?? new(); }
        catch (Exception ex) { Log.Error("gpu", ex); gpu = new(); }
        GpuUse GpuOf(uint pid) => gpu.TryGetValue(pid, out var u) ? u : default;

        // Disk and network per process: kernel trace when running (admin), else disk from I/O transfer counts.
        EtwIo.SetThreadOwners(_processes.ThreadOwners);
        long ioNow = System.Diagnostics.Stopwatch.GetTimestamp();
        double ioSecs = _ioStamp == 0 ? 0 : (ioNow - _ioStamp) / (double)System.Diagnostics.Stopwatch.Frequency;
        _ioStamp = ioNow;
        bool traced = EtwIo.Running;
        var io = traced ? EtwIo.Take() : null;
        double DiskOf(ProcessSample p) => io == null ? p.DiskBytesPerSec
            : ioSecs > 0 && io.TryGetValue(p.Pid, out var b) ? (b.DiskRead + b.DiskWrite) / ioSecs : 0;
        double? NetOf(uint pid) => io == null ? null
            : ioSecs > 0 && io.TryGetValue(pid, out var b) ? (b.NetSent + b.NetReceived) * 8 / ioSecs : 0;

        var alive = new HashSet<(uint, long)>(procs.Count);
        foreach (var p in procs) alive.Add((p.Pid, p.CreateTime));
        _images.Prune(alive);

        var groups = new Dictionary<string, List<(ProcessSample p, ImageInfo info)>>();
        var categories = new Dictionary<string, ProcessCategory>();
        foreach (var p in procs)
        {
            if (p.Pid == 0) continue; // Idle shows only on the Details page.
            string path = p.Pid == 4 ? "" : _images.AppPath(_images.GetPath(p.Pid, p.CreateTime));
            var info = _images.GetInfo(path, p.ImageName);

            bool isSvcHost = p.ImageName.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase);
            bool isWindows = s_kernelNames.Contains(p.ImageName)
                || (path.Length > 0 && path.StartsWith(s_windowsDir, StringComparison.OrdinalIgnoreCase));

            string key = isSvcHost ? $"svc:{p.Pid}:{p.CreateTime}"
                : (path.Length > 0 ? path : $"name:{p.ImageName}").ToLowerInvariant();

            if (!groups.TryGetValue(key, out var members)) groups[key] = members = new();
            members.Add((p, info));

            var cat = windows.ContainsKey(p.Pid) ? ProcessCategory.Apps
                : isWindows ? ProcessCategory.Windows : ProcessCategory.Background;
            if (!categories.TryGetValue(key, out var prev) || cat < prev) categories[key] = cat;
        }

        var result = new List<GroupData>(groups.Count);
        foreach (var (key, members) in groups)
        {
            var first = members[0];
            var cat = categories[key];
            string name = first.info.Description;
            var children = new List<ChildData>();

            if (key.StartsWith("svc:"))
            {
                _servicesByPid.TryGetValue(first.p.Pid, out var svcs);
                if (svcs is { Count: 1 }) name = "Service Host: " + svcs[0].DisplayName;
                else if (svcs is { Count: > 1 })
                {
                    name = "Service Host: " + svcs[0].DisplayName;
                    foreach (var s in svcs.OrderBy(s => s.DisplayName))
                        children.Add(new ChildData($"svc:{s.Name}", s.DisplayName, first.info.Path, s.Pid, 0, 0, 0, 0, "", false));
                }
            }
            else if (members.Count > 1)
            {
                foreach (var m in members)
                    children.Add(new ChildData($"pid:{m.p.Pid}:{m.p.CreateTime}", m.info.Description, m.info.Path,
                        m.p.Pid, m.p.CpuPercent, m.p.PrivateWorkingSet, DiskOf(m.p),
                        GpuOf(m.p.Pid).Percent, GpuOf(m.p.Pid).Engine ?? "", false, NetOf(m.p.Pid)));
            }
            else if (windows.TryGetValue(first.p.Pid, out var hwnds))
            {
                foreach (var h in hwnds)
                {
                    string title = User32.GetWindowTitle(h);
                    if (title.Length > 0)
                        children.Add(new ChildData($"hwnd:{h}", title, first.info.Path, first.p.Pid, 0, 0, 0, 0, "", true));
                }
            }

            // Like Task Manager: a group's GPU is the sum of its processes, labelled with the busiest one's engine.
            var busiest = members.Select(m => GpuOf(m.p.Pid)).MaxBy(u => u.Percent);

            nint mainWindow = 0;
            foreach (var m in members)
                if (windows.TryGetValue(m.p.Pid, out var hw)) { mainWindow = hw[0]; break; }

            result.Add(new GroupData(
                key, cat, name, first.info.Path, first.info.Publisher,
                members.Count == 1 ? first.p.Pid : 0,
                first.p.ImageName,
                Math.Min(100, members.Sum(m => m.p.CpuPercent)),
                members.Sum(m => m.p.PrivateWorkingSet),
                members.Sum(m => DiskOf(m.p)),
                Math.Min(100, members.Sum(m => GpuOf(m.p.Pid).Percent)),
                busiest.Percent > 0 ? busiest.Engine ?? "" : "",
                members.Count,
                children,
                members.Select(m => m.p.Pid).ToList(),
                mainWindow,
                io == null ? null : members.Sum(m => NetOf(m.p.Pid) ?? 0),
                members.All(m => m.p.Suspended)));
        }

        _owners.Prune(alive);
        var details = new List<ProcessDetail>(procs.Count);
        foreach (var p in procs)
        {
            string path = p.Pid is 0 or 4 ? "" : _images.GetPath(p.Pid, p.CreateTime);
            var info = _images.GetInfo(path, p.ImageName);
            var (user, arch) = _owners.Get(p.Pid, p.CreateTime);
            string desc = p.Pid switch { 0 => "Percentage of time the processor is idle", 4 => "NT Kernel & System", _ => info.Description };
            details.Add(new ProcessDetail(p, path, desc, user, arch, DiskOf(p), NetOf(p.Pid), GpuOf(p.Pid).Percent));
        }

        PerfSample? perf = null;
        try
        {
            _perf.CompressedBytes = procs.FirstOrDefault(p => p.ImageName == "Memory Compression")?.WorkingSet ?? 0;
            perf = _perf.Sample(_processes.TotalThreads, _processes.TotalHandles);
        }
        catch (Exception ex) { Log.Error("perf", ex); }

        double diskPct = perf?.Disks.Count > 0 ? perf.Disks.Max(d => d.ActivePercent) : 0;
        double? netPct = null;
        if (perf?.Adapters.Count > 0)
        {
            double link = perf.Adapters.Sum(a => (double)a.LinkSpeed);
            if (link > 0) netPct = Math.Min(100, perf.Adapters.Sum(a => a.SendBitsPerSec + a.ReceiveBitsPerSec) * 100 / link);
        }

        return new Snapshot(sys, _gpu is { Available: true } ? _gpu.TotalPercent : null, result, procs)
        {
            Details = details,
            Perf = perf,
            GpuByAdapter = _gpu?.AdapterPercent ?? new Dictionary<string, double>(),
            GpuEngines = _gpu?.EnginePercent ?? new Dictionary<string, IReadOnlyList<(string, double)>>(),
            GpuTemps = _gpu?.Temperatures ?? new Dictionary<string, double>(),
            Services = _services,
            DiskPercent = diskPct,
            NetworkPercent = netPct,
            IoTraced = traced,
        };
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        if (_thread.IsAlive && !_thread.Join(2000)) { EtwIo.Stop(); return; }   // still mid-sample: leave its buffers alone
        _processes.Dispose();
        _gpu?.Dispose();
        _perf.Dispose();
        EtwIo.Stop();
        _wake.Dispose();
    }
}
