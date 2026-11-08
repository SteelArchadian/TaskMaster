using System.Diagnostics;
using TaskManager.Engine;

// Usage: EngineProbe [ticks]  — prints per-tick cost and the busiest groups per category.
if (args.Length > 0 && args[0] == "--timing")
{
    // Cold-start cost of each source, in the order Sampler.Build calls them.
    var t = Stopwatch.StartNew();
    var svc = ServiceMap.Query(); Console.WriteLine($"services {svc.Count}: {t.ElapsedMilliseconds} ms"); t.Restart();
    using var ps = new ProcessSampler(); var list = ps.Sample(); Console.WriteLine($"processes {list.Count}: {t.ElapsedMilliseconds} ms"); t.Restart();
    var w = WindowScanner.Scan(); Console.WriteLine($"windows {w.Count}: {t.ElapsedMilliseconds} ms"); t.Restart();
    var cache = new ImageInfoCache();
    var paths = list.Select(p => cache.GetPath(p.Pid, p.CreateTime)).ToList(); Console.WriteLine($"paths: {t.ElapsedMilliseconds} ms"); t.Restart();
    var slow = new List<(long, string)>();
    foreach (var (p, path) in list.Zip(paths))
    {
        var one = Stopwatch.StartNew();
        cache.GetInfo(path, p.ImageName);
        if (one.ElapsedMilliseconds > 20) slow.Add((one.ElapsedMilliseconds, path));
    }
    Console.WriteLine($"version info: {t.ElapsedMilliseconds} ms");
    foreach (var s in slow.OrderByDescending(s => s.Item1).Take(8)) Console.WriteLine($"   {s.Item1} ms {s.Item2}");
    return;
}

if (args.Length > 0 && args[0] == "--gpu")
{
    using var ps = new ProcessSampler();
    using var gpu = new GpuSampler();
    Console.WriteLine($"adapters: {string.Join(", ", gpu.AdapterNames)}");
    for (int i = 0; i < 4; i++)
    {
        var list = ps.Sample();
        var t = Stopwatch.StartNew();
        long c0 = Process.GetCurrentProcess().TotalProcessorTime.Ticks;
        var use = gpu.Sample(list);
        Console.WriteLine($"tick {i}: {t.ElapsedMilliseconds} ms wall, {(Process.GetCurrentProcess().TotalProcessorTime.Ticks - c0) / 10000} ms cpu, total {gpu.TotalPercent:F1}%");
        if (i == 3)
            foreach (var (pid, u) in use.Where(kv => kv.Value.Percent > 0.05).OrderByDescending(kv => kv.Value.Percent).Take(8))
                Console.WriteLine($"   pid {pid,-6} {list.First(p => p.Pid == pid).ImageName,-28} {u.Percent,5:F1}%  {u.Engine}");
        Thread.Sleep(1000);
    }
    return;
}

int ticks = args.Length > 0 ? int.Parse(args[0]) : 5;
using var sampler = new Sampler { IntervalMs = 1000 };
var done = new ManualResetEventSlim();
int n = 0;
var sw = Stopwatch.StartNew();
long cpuBefore = Process.GetCurrentProcess().TotalProcessorTime.Ticks;

sampler.FrameReady += f =>
{
    n++;
    Console.WriteLine($"tick {n} at {sw.ElapsedMilliseconds} ms: {f.Processes.Count} processes, {f.Groups.Count} rows, " +
                      $"CPU {f.System.CpuPercent:F0}%, mem {f.System.MemoryPercent:F0}%");
    if (n == ticks)
    {
        foreach (var cat in Enum.GetValues<ProcessCategory>())
        {
            var g = f.Groups.Where(x => x.Category == cat).ToList();
            Console.WriteLine($"-- {cat} ({g.Count})");
            foreach (var x in g.OrderByDescending(x => x.Memory).Take(6))
                Console.WriteLine($"   {x.Name,-45} n={x.ProcessCount,-3} cpu={x.Cpu,5:F1}% mem={x.Memory / 1048576.0,8:F1} MB  kids={x.Children.Count}");
        }
        done.Set();
    }
};
sampler.Start();
done.Wait();
var self = Process.GetCurrentProcess();
Console.WriteLine($"probe CPU time {(self.TotalProcessorTime.Ticks - cpuBefore) / 10000} ms over {sw.ElapsedMilliseconds} ms; " +
                  $"private {self.PrivateMemorySize64 / 1048576} MB");
