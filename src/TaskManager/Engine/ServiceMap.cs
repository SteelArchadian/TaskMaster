using System.Runtime.InteropServices;
using TaskManager.Native;

namespace TaskManager.Engine;

public sealed record ServiceEntry(string Name, string DisplayName, uint Pid, uint State, uint Type);

/// <summary>All Win32 services with their host PID, from one EnumServicesStatusEx call.</summary>
public static unsafe class ServiceMap
{
    public static List<ServiceEntry> Query()
    {
        var list = new List<ServiceEntry>(400);
        nint scm = AdvApi32.OpenSCManager(null, null, AdvApi32.SC_MANAGER_CONNECT | AdvApi32.SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == 0) return list;
        try
        {
            uint resume = 0, size = 256 * 1024;
            while (true)
            {
                void* buf = NativeMemory.Alloc(size);
                try
                {
                    bool ok = AdvApi32.EnumServicesStatusEx(scm, AdvApi32.SC_ENUM_PROCESS_INFO, AdvApi32.SERVICE_WIN32,
                        AdvApi32.SERVICE_STATE_ALL, buf, size, out uint needed, out uint count, ref resume, 0);
                    int err = ok ? 0 : Marshal.GetLastPInvokeError();
                    var items = (AdvApi32.EnumServiceStatusProcess*)buf;
                    for (int i = 0; i < count; i++)
                    {
                        list.Add(new ServiceEntry(
                            Marshal.PtrToStringUni(items[i].ServiceName) ?? "",
                            Marshal.PtrToStringUni(items[i].DisplayName) ?? "",
                            items[i].ProcessId, items[i].CurrentState, items[i].ServiceType));
                    }
                    if (ok) break;
                    if (err != AdvApi32.ERROR_MORE_DATA) break;
                    size = Math.Max(size, needed);
                }
                finally { NativeMemory.Free(buf); }
            }
        }
        finally { AdvApi32.CloseServiceHandle(scm); }
        return list;
    }
}
