using TaskManager.Native;

namespace TaskManager.Engine;

public sealed record GpuEngine(int Adapter, int Node, string Name);

/// <summary>Per-process GPU use: the busiest engine's share, and which engine that was ("GPU 1 - 3D").</summary>
public readonly record struct GpuUse(double Percent, string Engine);

/// <summary>
/// GPU utilization from D3DKMT engine running times, the way Task Manager defines it: a process's GPU %
/// is its busiest engine, and the system total is the busiest engine overall.
/// Handles are kept open per process instance so each tick costs only the statistics calls.
/// </summary>
public sealed unsafe class GpuSampler : IDisposable
{
    private sealed class Adapter
    {
        public required uint Handle;
        public required uint LuidLow;
        public required int LuidHigh;
        public required int Index;
        public required string[] NodeNames;
        public long[] LastGlobal = [];
        public bool TempWorks = true;
    }

    // D3DKMT_ADAPTER_PERFDATA: PhysicalAdapterIndex, five ULONGLONG clocks/bandwidths, FanRPM, Power, Temperature (deci-°C).
    private const int KMTQAITYPE_ADAPTERPERFDATA = 62, PerfDataSize = 64, OffTemperature = 56;

    /// <summary>The driver-reported temperature Task Manager shows; drivers that don't support it are asked only once.</summary>
    private static double? Temperature(Adapter a)
    {
        byte* data = stackalloc byte[PerfDataSize];
        new Span<byte>(data, PerfDataSize).Clear();
        var q = new D3dkmt.QueryAdapterInfo { hAdapter = a.Handle, Type = KMTQAITYPE_ADAPTERPERFDATA, PrivateData = data, PrivateDataSize = PerfDataSize };
        uint deci = D3dkmt.D3DKMTQueryAdapterInfo(&q) == 0 ? *(uint*)(data + OffTemperature) : 0;
        if (deci == 0 || deci > 2000) { a.TempWorks = false; return null; }
        return deci / 10.0;
    }

    private readonly List<Adapter> _adapters = new();
    private readonly Dictionary<(uint pid, long ct), (nint handle, long[] last)> _procs = new();
    private long _lastStamp;

    public bool Available => _adapters.Count > 0;
    public IReadOnlyList<string> AdapterNames => _adapters.Select(a => $"GPU {a.Index}").ToList();
    public double TotalPercent { get; private set; }
    /// <summary>Busiest engine per adapter, keyed like the PDH instances: "luid_0x{high}_0x{low}".</summary>
    public IReadOnlyDictionary<string, double> AdapterPercent { get; private set; } = new Dictionary<string, double>();
    /// <summary>Per adapter, each engine type's utilization (nodes sharing a type report their busiest), in node order.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<(string, double)>> EnginePercent { get; private set; } = new Dictionary<string, IReadOnlyList<(string, double)>>();
    public IReadOnlyDictionary<string, double> Temperatures { get; private set; } = new Dictionary<string, double>();

    public GpuSampler()
    {
        try { Enumerate(); }
        catch (Exception ex) { Log.Error("gpu enumerate", ex); }
    }

    private void Enumerate()
    {
        var e = new D3dkmt.EnumAdapters2();
        if (D3dkmt.D3DKMTEnumAdapters2(&e) != 0 || e.NumAdapters == 0) return;
        var infos = new D3dkmt.AdapterInfo[e.NumAdapters];
        fixed (D3dkmt.AdapterInfo* p = infos)
        {
            e.Adapters = p;
            if (D3dkmt.D3DKMTEnumAdapters2(&e) != 0) return;
        }

        int index = 0;
        Dictionary<(uint, int, int), string>? types = null;
        foreach (var info in infos.Take((int)e.NumAdapters))
        {
            uint type = 0;
            var q = new D3dkmt.QueryAdapterInfo { hAdapter = info.hAdapter, Type = D3dkmt.KMTQAITYPE_ADAPTERTYPE, PrivateData = &type, PrivateDataSize = 4 };
            bool software = D3dkmt.D3DKMTQueryAdapterInfo(&q) == 0 && (type & 0x4) != 0;
            bool render = (type & 0x1) != 0;
            int nodes = software || !render ? 0 : NodeCount(info);
            if (nodes == 0)
            {
                uint h = info.hAdapter;
                D3dkmt.D3DKMTCloseAdapter(&h);
                continue;
            }

            types ??= EngineTypes();
            var names = new string[nodes];
            for (int n = 0; n < nodes; n++)
                names[n] = types.TryGetValue((info.LuidLow, info.LuidHigh, n), out var t) ? t : $"Engine {n}";
            _adapters.Add(new Adapter
            {
                Handle = info.hAdapter, LuidLow = info.LuidLow, LuidHigh = info.LuidHigh,
                Index = index++, NodeNames = names, LastGlobal = new long[nodes],
            });
        }
    }

    private static int NodeCount(D3dkmt.AdapterInfo info)
    {
        byte* q = stackalloc byte[D3dkmt.QueryStatisticsSize];
        new Span<byte>(q, D3dkmt.QueryStatisticsSize).Clear();
        *(int*)(q + D3dkmt.OffType) = D3dkmt.QUERYSTATISTICS_ADAPTER;
        *(uint*)(q + D3dkmt.OffLuid) = info.LuidLow;
        *(int*)(q + D3dkmt.OffLuid + 4) = info.LuidHigh;
        if (D3dkmt.D3DKMTQueryStatistics(q) != 0) return 0;
        return *(int*)(q + D3dkmt.OffResult + 4);
    }

    // KMTQAITYPE_NODEMETADATA is rejected by current drivers, so the engine type names come from the
    // "GPU Engine" performance object's instance names (…_luid_0x0000_0xABCD_phys_0_eng_3_engtype_Copy).
    // Read once at startup; no counters are opened or collected.
    private static Dictionary<(uint luidLow, int luidHigh, int node), string> EngineTypes()
    {
        var map = new Dictionary<(uint, int, int), string>();
        try
        {
            uint cLen = 0, iLen = 0;
            int st = Pdh.PdhEnumObjectItemsW(null, null, "GPU Engine", null, ref cLen, null, ref iLen, Pdh.PERF_DETAIL_WIZARD, 0);
            if (st != Pdh.PDH_MORE_DATA || iLen == 0) return map;
            var counters = new char[cLen + 1];
            var instances = new char[iLen + 1];
            fixed (char* c = counters)
            fixed (char* i = instances)
                st = Pdh.PdhEnumObjectItemsW(null, null, "GPU Engine", c, ref cLen, i, ref iLen, Pdh.PERF_DETAIL_WIZARD, 0);
            if (st != 0) return map;
            foreach (var name in new string(instances, 0, (int)iLen).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = name.Split('_');
                int luid = Array.IndexOf(parts, "luid"), eng = Array.IndexOf(parts, "eng"), type = Array.IndexOf(parts, "engtype");
                if (luid < 0 || eng < 0 || type < 0 || luid + 2 >= parts.Length || eng + 1 >= parts.Length) continue;
                int high = Convert.ToInt32(parts[luid + 1], 16);
                uint low = Convert.ToUInt32(parts[luid + 2], 16);
                if (!int.TryParse(parts[eng + 1], out int node)) continue;
                // "VideoDecode" -> "Video Decode", "Compute_0" -> "Compute 0", as Task Manager labels them.
                string engType = System.Text.RegularExpressions.Regex.Replace(
                    string.Join(' ', parts.Skip(type + 1)).Trim(), "(?<=[a-z])(?=[A-Z])", " ");
                if (engType.Length > 0) map[(low, high, node)] = engType;
            }
        }
        catch (Exception ex) { Log.Error("gpu engine names", ex); }
        return map;
    }

    private static long RunningTime(Adapter a, int type, nint process, int node)
    {
        byte* q = stackalloc byte[D3dkmt.QueryStatisticsSize];
        new Span<byte>(q, D3dkmt.QueryStatisticsSize).Clear();
        *(int*)(q + D3dkmt.OffType) = type;
        *(uint*)(q + D3dkmt.OffLuid) = a.LuidLow;
        *(int*)(q + D3dkmt.OffLuid + 4) = a.LuidHigh;
        *(nint*)(q + D3dkmt.OffProcess) = process;
        *(uint*)(q + D3dkmt.OffQueryParam) = (uint)node;
        if (D3dkmt.D3DKMTQueryStatistics(q) != 0) return -1;
        return *(long*)(q + D3dkmt.OffResult);
    }

    /// <summary>Samples all processes. Returns per-process use keyed by PID; empty if no GPU is readable.</summary>
    public Dictionary<uint, GpuUse> Sample(IReadOnlyList<ProcessSample> processes)
    {
        var result = new Dictionary<uint, GpuUse>();
        if (_adapters.Count == 0) return result;

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double elapsed100ns = _lastStamp == 0 ? 0 : (now - _lastStamp) * 10_000_000.0 / System.Diagnostics.Stopwatch.Frequency;
        _lastStamp = now;

        // System total = busiest engine.
        double total = 0;
        var perAdapter = new Dictionary<string, double>();
        var perEngine = new Dictionary<string, IReadOnlyList<(string, double)>>();
        var temps = new Dictionary<string, double>();
        foreach (var a in _adapters)
        {
            double busiest = 0;
            var engines = new List<(string name, double pct)>();
            for (int n = 0; n < a.NodeNames.Length; n++)
            {
                long t = RunningTime(a, D3dkmt.QUERYSTATISTICS_NODE, 0, n);
                double pct = t >= 0 && elapsed100ns > 0 && a.LastGlobal[n] > 0 ? Math.Clamp((t - a.LastGlobal[n]) / elapsed100ns * 100, 0, 100) : 0;
                busiest = Math.Max(busiest, pct);
                if (t >= 0) a.LastGlobal[n] = t;
                int at = engines.FindIndex(e => e.name == a.NodeNames[n]);
                if (at < 0) engines.Add((a.NodeNames[n], pct));
                else if (pct > engines[at].pct) engines[at] = (a.NodeNames[n], pct);
            }
            string key = $"luid_0x{a.LuidHigh:X8}_0x{a.LuidLow:X8}";
            perAdapter[key] = Math.Clamp(busiest, 0, 100);
            perEngine[key] = engines;
            if (a.TempWorks && Temperature(a) is double c) temps[key] = c;
            total = Math.Max(total, busiest);
        }
        AdapterPercent = perAdapter;
        EnginePercent = perEngine;
        Temperatures = temps;
        TotalPercent = Math.Clamp(total, 0, 100);

        int engineCount = _adapters.Sum(a => a.NodeNames.Length);
        var alive = new HashSet<(uint, long)>();
        foreach (var p in processes)
        {
            if (p.Pid <= 4) continue;
            var key = (p.Pid, p.CreateTime);
            alive.Add(key);
            if (!_procs.TryGetValue(key, out var entry))
            {
                // D3DKMT rejects PROCESS_QUERY_LIMITED_INFORMATION handles; it needs full query rights.
                nint h = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_INFORMATION, false, p.Pid);
                entry = (h, new long[engineCount]);
                _procs[key] = entry;
            }
            if (entry.handle == 0) continue;

            double best = 0;
            string bestName = "";
            int slot = 0;
            foreach (var a in _adapters)
            {
                if (RunningTime(a, D3dkmt.QUERYSTATISTICS_PROCESS, entry.handle, 0) < 0) { slot += a.NodeNames.Length; continue; }
                for (int n = 0; n < a.NodeNames.Length; n++, slot++)
                {
                    long t = RunningTime(a, D3dkmt.QUERYSTATISTICS_PROCESS_NODE, entry.handle, n);
                    if (t < 0) continue;
                    long prev = entry.last[slot];
                    entry.last[slot] = t;
                    if (elapsed100ns <= 0 || prev == 0 || t <= prev) continue;
                    double pct = (t - prev) / elapsed100ns * 100;
                    if (pct > best) { best = pct; bestName = $"GPU {a.Index} - {a.NodeNames[n]}"; }
                }
            }
            result[p.Pid] = new GpuUse(Math.Min(best, 100), bestName);
        }

        foreach (var k in _procs.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            if (_procs[k].handle != 0) Kernel32.CloseHandle(_procs[k].handle);
            _procs.Remove(k);
        }
        return result;
    }

    public void Dispose()
    {
        foreach (var (h, _) in _procs.Values) if (h != 0) Kernel32.CloseHandle(h);
        _procs.Clear();
        foreach (var a in _adapters) { uint h = a.Handle; D3dkmt.D3DKMTCloseAdapter(&h); }
        _adapters.Clear();
    }
}
