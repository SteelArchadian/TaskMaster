using System.Runtime.InteropServices;

namespace TaskManager.Native;

/// <summary>Event Tracing for Windows: a real-time kernel session for per-process disk and network bytes.</summary>
internal static unsafe partial class Etw
{
    public const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
    public const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
    public const uint EVENT_TRACE_SYSTEM_LOGGER_MODE = 0x02000000;
    public const uint EVENT_TRACE_FLAG_DISK_IO = 0x00000100;
    public const uint EVENT_TRACE_FLAG_NETWORK_TCPIP = 0x00010000;
    public const uint EVENT_TRACE_CONTROL_STOP = 1;
    public const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
    public const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
    public const ulong INVALID_PROCESSTRACE_HANDLE = ulong.MaxValue;
    public const int ERROR_ALREADY_EXISTS = 183;

    /// <summary>EVENT_TRACE_PROPERTIES (x64, 120 bytes); the session name follows it in the same buffer.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 120)]
    public struct TraceProperties
    {
        [FieldOffset(0)] public uint WnodeBufferSize;
        [FieldOffset(24)] public Guid WnodeGuid;
        [FieldOffset(40)] public uint WnodeClientContext;
        [FieldOffset(44)] public uint WnodeFlags;
        [FieldOffset(48)] public uint BufferSize;
        [FieldOffset(52)] public uint MinimumBuffers;
        [FieldOffset(56)] public uint MaximumBuffers;
        [FieldOffset(60)] public uint MaximumFileSize;
        [FieldOffset(64)] public uint LogFileMode;
        [FieldOffset(68)] public uint FlushTimer;
        [FieldOffset(72)] public uint EnableFlags;
        [FieldOffset(112)] public uint LogFileNameOffset;
        [FieldOffset(116)] public uint LoggerNameOffset;
    }

    /// <summary>EVENT_TRACE_LOGFILEW (x64, 448 bytes), only the fields we set.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 448)]
    public struct TraceLogfile
    {
        [FieldOffset(0)] public char* LogFileName;
        [FieldOffset(8)] public char* LoggerName;
        [FieldOffset(28)] public uint ProcessTraceMode;
        [FieldOffset(424)] public delegate* unmanaged<EventRecord*, void> EventRecordCallback;
        [FieldOffset(440)] public nint Context;
    }

    /// <summary>EVENT_RECORD (x64): EVENT_HEADER, buffer context, then the payload pointer.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 112)]
    public struct EventRecord
    {
        [FieldOffset(8)] public uint ThreadId;
        [FieldOffset(12)] public uint ProcessId;
        [FieldOffset(24)] public Guid ProviderId;
        [FieldOffset(45)] public byte Opcode;
        [FieldOffset(86)] public ushort UserDataLength;
        [FieldOffset(96)] public byte* UserData;
    }

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint StartTraceW(out ulong handle, string name, TraceProperties* properties);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint ControlTraceW(ulong handle, string name, TraceProperties* properties, uint control);

    [LibraryImport("advapi32.dll")]
    public static partial ulong OpenTraceW(TraceLogfile* logfile);

    [LibraryImport("advapi32.dll")]
    public static partial uint ProcessTrace(ulong* handles, uint count, nint start, nint end);

    [LibraryImport("advapi32.dll")]
    public static partial uint CloseTrace(ulong handle);
}
