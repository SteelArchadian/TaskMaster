using TaskManager.Native;

namespace TaskManager.Engine;

/// <summary>
/// Command lines for the "Command line" column, read only while that column is shown. A process's command line
/// never changes, so each PID is read once (keyed with its start time so a reused PID is read again).
/// </summary>
public static unsafe class CommandLines
{
    private static readonly Dictionary<(uint pid, long start), string> s_cache = new();

    public static string Get(uint pid, long createTime)
    {
        if (pid == 0) return "";
        if (s_cache.TryGetValue((pid, createTime), out var s)) return s;
        s = Read(pid);
        if (s_cache.Count > 4096) s_cache.Clear();
        s_cache[(pid, createTime)] = s;
        return s;
    }

    private static string Read(uint pid)
    {
        nint h = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return "";
        try
        {
            NtDll.NtQueryInformationProcess(h, NtDll.ProcessCommandLineInformation, null, 0, out uint need);
            if (need == 0 || need > 1 << 20) return "";
            var mem = new byte[need];
            fixed (byte* p = mem)
            {
                if (NtDll.NtQueryInformationProcess(h, NtDll.ProcessCommandLineInformation, p, need, out _) != 0) return "";
                // UNICODE_STRING { ushort Length; ushort MaxLength; (pad) char* Buffer } followed by the text.
                ushort len = *(ushort*)p;
                char* text = *(char**)(p + 8);
                return len == 0 || text == null ? "" : new string(text, 0, len / 2);
            }
        }
        finally { Kernel32.CloseHandle(h); }
    }
}
