namespace TaskManager.Engine;

/// <summary>The last 60 samples of every Performance graph, recorded on each frame even while the page is hidden.</summary>
public static class PerfHistory
{
    public const int Points = 60;

    public sealed class Series
    {
        private readonly double[] _v = new double[Points];
        public int Count { get; private set; }

        /// <summary>Oldest first; index Points-1 is the newest.</summary>
        public double this[int i] => _v[i];
        public double Latest => _v[Points - 1];
        public double Max => _v.Max();

        public void Push(double v)
        {
            Array.Copy(_v, 1, _v, 0, Points - 1);
            _v[Points - 1] = double.IsFinite(v) ? v : 0;
            if (Count < Points) Count++;
        }
    }

    private static readonly Dictionary<string, Series> s_series = [];

    public static event Action? Updated;

    public static Series Get(string key)
    {
        if (!s_series.TryGetValue(key, out var s)) s_series[key] = s = new Series();
        return s;
    }

    public static void Add(Snapshot f)
    {
        Get("CPU").Push(f.System.CpuPercent);
        Get("Memory").Push(f.System.MemoryPercent);
        if (f.Perf is { } p)
        {
            for (int i = 0; i < p.CoreCpu.Length; i++) Get("CPU#" + i).Push(p.CoreCpu[i]);
            Get("Memory.Commit").Push(p.Memory.CommitLimit == 0 ? 0 : p.Memory.Committed * 100.0 / p.Memory.CommitLimit);
            foreach (var d in p.Disks)
            {
                Get($"Disk{d.Number}").Push(d.ActivePercent);
                Get($"Disk{d.Number}.R").Push(d.ReadBytesPerSec);
                Get($"Disk{d.Number}.W").Push(d.WriteBytesPerSec);
            }
            foreach (var a in p.Adapters)
            {
                Get($"Net{a.Id}.S").Push(a.SendBitsPerSec);
                Get($"Net{a.Id}.R").Push(a.ReceiveBitsPerSec);
            }
            for (int i = 0; i < p.Gpus.Count; i++)
            {
                var g = p.Gpus[i];
                Get($"GPU{i}").Push(f.GpuByAdapter.GetValueOrDefault(g.Luid));
                Get($"GPU{i}.Ded").Push(g.DedicatedUsed);
                Get($"GPU{i}.Sh").Push(g.SharedUsed);
                if (f.GpuEngines.TryGetValue(g.Luid, out var engines))
                    foreach (var (name, pct) in engines) Get($"GPU{i}.E.{name}").Push(pct);
            }
        }
        Updated?.Invoke();
    }
}
