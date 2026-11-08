using System.Runtime.InteropServices;
using System.Security.Principal;
using TaskManager.Native;

namespace TaskManager.Engine;

/// <summary>User name and architecture per process instance, looked up once (Details page).</summary>
public sealed unsafe partial class ProcessOwner
{
    private readonly Dictionary<(uint, long), (string user, string arch)> _cache = new();
    private readonly Dictionary<string, string> _names = new();
    private static readonly string s_native = RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => "ARM64",
        Architecture.X86 => "x86",
        _ => "x64",
    };

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(nint token, int infoClass, void* info, uint length, out uint returned);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWow64Process2(nint process, out ushort processMachine, out ushort nativeMachine);

    public (string user, string arch) Get(uint pid, long createTime)
    {
        if (_cache.TryGetValue((pid, createTime), out var v)) return v;
        string user = pid is 0 or 4 ? "SYSTEM" : "", arch = pid is 0 or 4 ? s_native : "";
        nint h = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h != 0)
        {
            try
            {
                if (IsWow64Process2(h, out ushort pm, out _))
                    arch = pm switch { 0 => s_native, 0x14c => "x86", 0x1c4 => "ARM", _ => s_native };
                if (OpenProcessToken(h, 0x0008 /* TOKEN_QUERY */, out nint token))
                {
                    try
                    {
                        byte* buf = stackalloc byte[256];
                        if (GetTokenInformation(token, 1 /* TokenUser */, buf, 256, out _))
                            user = NameOf(*(nint*)buf);
                    }
                    finally { Kernel32.CloseHandle(token); }
                }
            }
            finally { Kernel32.CloseHandle(h); }
        }
        v = (user, arch);
        _cache[(pid, createTime)] = v;
        return v;
    }

    private string NameOf(nint sid)
    {
        var s = new SecurityIdentifier(sid);
        string key = s.Value;
        if (_names.TryGetValue(key, out var n)) return n;
        try
        {
            n = ((NTAccount)s.Translate(typeof(NTAccount))).Value;
            int slash = n.IndexOf('\\');
            if (slash >= 0) n = n[(slash + 1)..];
        }
        catch (IdentityNotMappedException) { n = key; }
        _names[key] = n;
        return n;
    }

    public void Prune(HashSet<(uint, long)> alive)
    {
        if (_cache.Count < alive.Count * 2) return;
        foreach (var k in _cache.Keys.Where(k => !alive.Contains(k)).ToList()) _cache.Remove(k);
    }
}
