using System.Runtime.InteropServices;
using TaskManager.Native;

namespace TaskManager.Engine;

/// <summary>Start / stop / restart a service, and its svchost group, through the Service Control Manager.</summary>
public static unsafe partial class ServiceControl
{
    private const uint SERVICE_QUERY_CONFIG = 0x1, SERVICE_QUERY_STATUS = 0x4, SERVICE_START = 0x10, SERVICE_STOP = 0x20;
    private const uint SERVICE_CONTROL_STOP = 1;
    public const uint Stopped = 1, StartPending = 2, StopPending = 3, Running = 4, ContinuePending = 5, PausePending = 6, Paused = 7;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus { public uint Type, State, Controls, ExitCode, SpecificExitCode, CheckPoint, WaitHint; }

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenService(nint scm, string name, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartService(nint service, uint argc, nint argv);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ControlService(nint service, uint control, ServiceStatus* status);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatus(nint service, ServiceStatus* status);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceConfig(nint service, void* config, uint size, out uint needed);

    private static readonly Dictionary<string, string> s_groups = new(StringComparer.OrdinalIgnoreCase);

    public static string StateName(uint state) => state switch
    {
        Stopped => "Stopped",
        StartPending => "Starting",
        StopPending => "Stopping",
        Running => "Running",
        ContinuePending => "Continuing",
        PausePending => "Pausing",
        Paused => "Paused",
        _ => "",
    };

    /// <summary>The "-k group" a shared-process service runs under, read once per service.</summary>
    public static string GroupOf(string name)
    {
        lock (s_groups)
        {
            if (s_groups.TryGetValue(name, out var g)) return g;
            g = "";
            nint scm = AdvApi32.OpenSCManager(null, null, AdvApi32.SC_MANAGER_CONNECT);
            if (scm != 0)
            {
                nint svc = OpenService(scm, name, SERVICE_QUERY_CONFIG);
                if (svc != 0)
                {
                    byte* buf = stackalloc byte[8192];
                    if (QueryServiceConfig(svc, buf, 8192, out _))
                    {
                        string path = Marshal.PtrToStringUni(*(nint*)(buf + 16)) ?? "";
                        int k = path.IndexOf(" -k ", StringComparison.OrdinalIgnoreCase);
                        if (k >= 0)
                        {
                            g = path[(k + 4)..].Trim();
                            int sp = g.IndexOf(' ');
                            if (sp > 0) g = g[..sp];
                        }
                    }
                    AdvApi32.CloseServiceHandle(svc);
                }
                AdvApi32.CloseServiceHandle(scm);
            }
            s_groups[name] = g;
            return g;
        }
    }

    /// <summary>Runs off the UI thread. Returns null on success or the error text.</summary>
    public static Task<string?> StartAsync(string name) => Task.Run(() => With(name, SERVICE_START | SERVICE_QUERY_STATUS, h =>
        StartService(h, 0, 0) ? null : Error()));

    public static Task<string?> StopAsync(string name) => Task.Run(() => With(name, SERVICE_STOP | SERVICE_QUERY_STATUS, h =>
    {
        ServiceStatus st;
        if (!ControlService(h, SERVICE_CONTROL_STOP, &st)) return Error();
        return WaitFor(h, Stopped, 20_000) ? null : "The service did not stop in time.";
    }));

    public static Task<string?> RestartAsync(string name) => Task.Run(() => With(name, SERVICE_STOP | SERVICE_START | SERVICE_QUERY_STATUS, h =>
    {
        ServiceStatus st;
        if (QueryServiceStatus(h, &st) && st.State != Stopped)
        {
            if (!ControlService(h, SERVICE_CONTROL_STOP, &st)) return Error();
            if (!WaitFor(h, Stopped, 20_000)) return "The service did not stop in time.";
        }
        return StartService(h, 0, 0) ? null : Error();
    }));

    private static bool WaitFor(nint h, uint state, int timeoutMs)
    {
        long end = Environment.TickCount64 + timeoutMs;
        ServiceStatus st;
        while (Environment.TickCount64 < end)
        {
            if (!QueryServiceStatus(h, &st)) return false;
            if (st.State == state) return true;
            Thread.Sleep(200);
        }
        return false;
    }

    private static string Error() => new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()).Message;

    private static string? With(string name, uint access, Func<nint, string?> action)
    {
        nint scm = AdvApi32.OpenSCManager(null, null, AdvApi32.SC_MANAGER_CONNECT);
        if (scm == 0) return Error();
        try
        {
            nint svc = OpenService(scm, name, access);
            if (svc == 0) return Error();
            try { return action(svc); }
            finally { AdvApi32.CloseServiceHandle(svc); }
        }
        finally { AdvApi32.CloseServiceHandle(scm); }
    }
}
