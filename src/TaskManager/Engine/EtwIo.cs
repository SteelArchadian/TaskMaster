using System.Runtime.InteropServices;
using TaskManager.Native;

namespace TaskManager.Engine;

/// <summary>Bytes moved by one process since the last <see cref="EtwIo.Take"/>.</summary>
public struct IoBytes
{
    public long DiskRead, DiskWrite, NetSent, NetReceived;
}

/// <summary>
/// Per-process disk and network bytes from a real-time kernel trace (the same source Task Manager and Resource
/// Monitor use). Needs admin; without it <see cref="Running"/> stays false and callers fall back.
/// The session has its own name so it never clashes with the "NT Kernel Logger" another tool may own, and it is
/// always stopped on exit because real-time sessions outlive the process that started them.
/// </summary>
public static unsafe class EtwIo
{
    private const string SessionName = "SteelArchadian TaskManager Kernel";
    private static readonly Guid s_tcp = new("9a280ac0-c8e0-11d1-84e2-00c04fb998a2");
    private static readonly Guid s_udp = new("bf3a50c5-a9c9-4988-a005-2df0b7c80f80");
    private static readonly Guid s_disk = new("3d6fa8d4-fe05-11d0-9dda-00c04fd7ba7c");

    private static readonly object s_lock = new();
    private static Dictionary<uint, IoBytes> s_bytes = new();
    private static Dictionary<uint, IoBytes> s_spare = new();
    private static volatile Dictionary<uint, uint> s_threadOwner = new();
    private static ulong s_session, s_trace = Etw.INVALID_PROCESSTRACE_HANDLE;
    private static Thread? s_thread;
    private static long s_lastEvent;

    public static bool Running { get; private set; }

    /// <summary>Starts the session once. Returns false (and logs why) when it can't, e.g. without admin.</summary>
    public static bool Start()
    {
        if (Running) return true;
        try
        {
            uint err = StartSession();
            if (err == Etw.ERROR_ALREADY_EXISTS)
            {
                // Left over from a crash of ours: same name, so it is ours to stop.
                StopSession();
                err = StartSession();
            }
            if (err != 0) { Log.Info($"etw: StartTrace failed ({err}); disk/network fall back"); return false; }

            var logfile = new Etw.TraceLogfile();
            fixed (char* name = SessionName)
            {
                logfile.LoggerName = name;
                logfile.ProcessTraceMode = Etw.PROCESS_TRACE_MODE_REAL_TIME | Etw.PROCESS_TRACE_MODE_EVENT_RECORD;
                logfile.EventRecordCallback = &OnEvent;
                s_trace = Etw.OpenTraceW(&logfile);
            }
            if (s_trace == Etw.INVALID_PROCESSTRACE_HANDLE)
            {
                Log.Info($"etw: OpenTrace failed ({Marshal.GetLastSystemError()})");
                StopSession();
                return false;
            }
            s_thread = new Thread(() =>
            {
                ulong h = s_trace;
                uint r = Etw.ProcessTrace(&h, 1, 0, 0);
                if (r != 0) Log.Info($"etw: ProcessTrace ended ({r})");
            }) { IsBackground = true, Name = "ETW" };
            s_thread.Start();
            Running = true;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
            Log.Info("etw: kernel disk/network trace running");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("etw start", ex);
            StopSession();
            return false;
        }
    }

    public static void Stop()
    {
        if (!Running) return;
        Running = false;
        StopSession();
        if (s_trace != Etw.INVALID_PROCESSTRACE_HANDLE) { Etw.CloseTrace(s_trace); s_trace = Etw.INVALID_PROCESSTRACE_HANDLE; }
    }

    /// <summary>True when events arrived recently; a session killed from outside stops counting as live.</summary>
    public static bool Live => Running && Environment.TickCount64 - Interlocked.Read(ref s_lastEvent) < 10_000;

    /// <summary>Thread id → process id, refreshed every tick from the process snapshot (disk events carry a thread).</summary>
    public static void SetThreadOwners(Dictionary<uint, uint> map) => s_threadOwner = map;

    /// <summary>Everything counted since the previous call, per process id.</summary>
    public static Dictionary<uint, IoBytes> Take()
    {
        lock (s_lock)
        {
            var taken = s_bytes;
            s_spare.Clear();
            s_bytes = s_spare;
            s_spare = taken;
            return taken;
        }
    }

    private static uint StartSession()
    {
        int size = sizeof(Etw.TraceProperties) + (SessionName.Length + 1) * 2;
        byte* buf = stackalloc byte[size];
        new Span<byte>(buf, size).Clear();
        var p = (Etw.TraceProperties*)buf;
        p->WnodeBufferSize = (uint)size;
        p->WnodeFlags = Etw.WNODE_FLAG_TRACED_GUID;
        p->WnodeClientContext = 1; // QueryPerformanceCounter timestamps
        p->BufferSize = 64;        // KB
        p->MinimumBuffers = 4;
        p->MaximumBuffers = 32;
        p->FlushTimer = 1;         // deliver at least once a second
        p->LogFileMode = Etw.EVENT_TRACE_REAL_TIME_MODE | Etw.EVENT_TRACE_SYSTEM_LOGGER_MODE;
        p->EnableFlags = Etw.EVENT_TRACE_FLAG_DISK_IO | Etw.EVENT_TRACE_FLAG_NETWORK_TCPIP;
        p->LoggerNameOffset = (uint)sizeof(Etw.TraceProperties);
        return Etw.StartTraceW(out s_session, SessionName, p);
    }

    private static void StopSession()
    {
        int size = sizeof(Etw.TraceProperties) + 1024 * 2 * 2;
        byte* buf = stackalloc byte[size];
        new Span<byte>(buf, size).Clear();
        var p = (Etw.TraceProperties*)buf;
        p->WnodeBufferSize = (uint)size;
        p->LoggerNameOffset = (uint)sizeof(Etw.TraceProperties);
        p->LogFileNameOffset = (uint)sizeof(Etw.TraceProperties) + 2048;
        Etw.ControlTraceW(0, SessionName, p, Etw.EVENT_TRACE_CONTROL_STOP);
    }

    [UnmanagedCallersOnly]
    private static void OnEvent(Etw.EventRecord* e)
    {
        try
        {
            byte op = e->Opcode;
            byte* d = e->UserData;
            if (d == null) return;
            Guid g = e->ProviderId;
            uint pid; long bytes; int kind;

            if (g == s_tcp || g == s_udp)
            {
                // TcpIp/UdpIp TypeGroup1: PID u32 @0, size u32 @4. 10/11 = IPv4 send/recv, 26/27 = IPv6.
                if (e->UserDataLength < 8) return;
                if (op is 10 or 26) kind = 2;
                else if (op is 11 or 27) kind = 3;
                else return;
                pid = *(uint*)d;
                bytes = *(uint*)(d + 4);
            }
            else if (g == s_disk)
            {
                // DiskIo TypeGroup1 (x64): TransferSize u32 @8, IssuingThreadId u32 @48. 10 = read, 11 = write.
                if (op is not (10 or 11) || e->UserDataLength < 52) return;
                kind = op == 10 ? 0 : 1;
                bytes = *(uint*)(d + 8);
                uint tid = *(uint*)(d + 48);
                if (!s_threadOwner.TryGetValue(tid, out pid)) pid = e->ProcessId;
                if (pid == uint.MaxValue) return;
            }
            else return;

            Interlocked.Exchange(ref s_lastEvent, Environment.TickCount64);
            lock (s_lock)
            {
                ref var b = ref CollectionsMarshal.GetValueRefOrAddDefault(s_bytes, pid, out _);
                switch (kind)
                {
                    case 0: b.DiskRead += bytes; break;
                    case 1: b.DiskWrite += bytes; break;
                    case 2: b.NetSent += bytes; break;
                    default: b.NetReceived += bytes; break;
                }
            }
        }
        catch { /* never let an exception cross into native code */ }
    }
}
