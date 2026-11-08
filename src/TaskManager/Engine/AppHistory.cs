using System.Diagnostics;
using System.Text.Json;

namespace TaskManager.Engine;

public sealed class AppUsage
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public long CpuTime100ns { get; set; }
    public double NetworkBytes { get; set; }
    public double DiskBytes { get; set; }
}

public sealed class AppHistoryData
{
    // A new history starts at boot: CPU time of processes already running is counted in full.
    public DateTime Since { get; set; } = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
    /// <summary>FILETIME of the last frame recorded; processes created after it have all their CPU time uncounted.</summary>
    public long LastSeen { get; set; }
    public Dictionary<string, AppUsage> Apps { get; set; } = [];
}

/// <summary>
/// Per-app CPU time, network and disk use, added up from every frame and kept in our own file. Windows keeps its
/// own numbers in a database only the system can open, so we record ours from the day this app first ran.
/// </summary>
public static class AppHistory
{
    private static readonly string s_file = System.IO.Path.Combine(Settings.Folder, "apphistory.json");
    private static readonly Dictionary<(uint, long), long> s_lastCpu = [];
    private static readonly Stopwatch s_clock = new();
    private static long _lastSave;
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = false };

    public static AppHistoryData Data { get; private set; } = Load();

    private static AppHistoryData Load()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Save();
        try
        {
            if (File.Exists(s_file)) return JsonSerializer.Deserialize<AppHistoryData>(File.ReadAllText(s_file), s_json) ?? new();
        }
        catch (Exception ex) { Log.Error("app history load", ex); }
        return new();
    }

    public static void Add(Snapshot f)
    {
        double seconds = s_clock.IsRunning ? Math.Min(5, s_clock.Elapsed.TotalSeconds) : 0;
        s_clock.Restart();
        var alive = new HashSet<(uint, long)>();
        foreach (var d in f.Details)
        {
            if (d.S.Pid <= 4) continue;
            var id = (d.S.Pid, d.S.CreateTime);
            alive.Add(id);
            long cpuDelta = s_lastCpu.TryGetValue(id, out long last) ? Math.Max(0, d.S.CpuTime100ns - last)
                : d.S.CreateTime >= Data.LastSeen ? d.S.CpuTime100ns : 0;
            s_lastCpu[id] = d.S.CpuTime100ns;
            double net = (d.Network ?? 0) / 8 * seconds, disk = d.Disk * seconds;
            if (cpuDelta == 0 && net == 0 && disk == 0) continue;
            string key = (d.Path.Length > 0 ? d.Path : d.S.ImageName).ToLowerInvariant();
            if (!Data.Apps.TryGetValue(key, out var u)) Data.Apps[key] = u = new AppUsage { Path = d.Path };
            if (d.Description.Length > 0) u.Name = d.Description;
            else if (u.Name.Length == 0) u.Name = d.S.ImageName;
            u.CpuTime100ns += cpuDelta;
            u.NetworkBytes += net;
            u.DiskBytes += disk;
        }
        Data.LastSeen = DateTime.UtcNow.ToFileTimeUtc();
        foreach (var gone in s_lastCpu.Keys.Where(k => !alive.Contains(k)).ToList()) s_lastCpu.Remove(gone);
        if (Environment.TickCount64 - _lastSave > 60_000) Save();
    }

    public static void Clear()
    {
        Data = new AppHistoryData { Since = DateTime.Now, LastSeen = DateTime.UtcNow.ToFileTimeUtc() };
        Save();
    }

    public static void Save()
    {
        _lastSave = Environment.TickCount64;
        if (Settings.ReadOnly) return;
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            string tmp = s_file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Data, s_json));
            File.Move(tmp, s_file, overwrite: true);
        }
        catch (Exception ex) { Log.Error("app history save", ex); }
    }
}
