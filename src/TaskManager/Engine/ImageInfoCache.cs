using System.Collections.Concurrent;
using System.Diagnostics;
using TaskManager.Native;

namespace TaskManager.Engine;

public sealed record ImageInfo(string Path, string Description, string Publisher);

/// <summary>
/// Path per process instance and description/publisher per exe, each looked up once.
/// Lookups that fail (protected processes) are remembered so they are never retried every tick.
/// </summary>
public sealed class ImageInfoCache
{
    private readonly Dictionary<(uint, long), string> _paths = new();
    private readonly ConcurrentDictionary<string, ImageInfo> _infos = new(StringComparer.OrdinalIgnoreCase);

    public unsafe string GetPath(uint pid, long createTime)
    {
        var key = (pid, createTime);
        if (_paths.TryGetValue(key, out var path)) return path;

        path = "";
        nint h = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h != 0)
        {
            try
            {
                char* buf = stackalloc char[1024];
                uint size = 1024;
                if (Kernel32.QueryFullProcessImageName(h, 0, buf, ref size))
                    path = new string(buf, 0, (int)size);
            }
            finally { Kernel32.CloseHandle(h); }
        }
        _paths[key] = path;
        return path;
    }

    private readonly ConcurrentDictionary<string, string> _apps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The exe a process really belongs to. Self-updating apps rename their running exe aside ("app.exe.old-123",
    /// "app.exe.bak") so the new one can take its place; those processes are still that app, so they group with it
    /// and take its icon. Falls back to the path itself when there's no such app exe next to it.
    /// </summary>
    public string AppPath(string path)
    {
        if (path.Length == 0) return path;
        return _apps.GetOrAdd(path, p =>
        {
            int i = p.LastIndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (i <= 0 || i + 4 >= p.Length) return p;
            string app = p[..(i + 4)];
            return File.Exists(app) ? app : p;
        });
    }

    /// <summary>Drops entries for processes that have exited.</summary>
    public void Prune(HashSet<(uint, long)> alive)
    {
        if (_paths.Count < alive.Count * 2) return;
        foreach (var k in _paths.Keys.Where(k => !alive.Contains(k)).ToList()) _paths.Remove(k);
    }

    public ImageInfo GetInfo(string path, string imageName)
    {
        if (path.Length == 0) return new ImageInfo("", imageName, "");
        return _infos.GetOrAdd(path, p =>
        {
            string desc = "", company = "";
            try
            {
                var v = FileVersionInfo.GetVersionInfo(p);
                desc = v.FileDescription?.Trim() ?? "";
                company = v.CompanyName?.Trim() ?? "";
            }
            catch (Exception) { /* no version resource: fall back to the file name, as Task Manager does */ }
            if (desc.Length == 0) desc = imageName;
            return new ImageInfo(p, desc, company);
        });
    }
}
