using System.Runtime.InteropServices;

namespace TaskManager.Native;

internal static partial class NtDll
{
    public const int SystemProcessInformation = 5;
    public const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;

    [LibraryImport("ntdll.dll")]
    public static unsafe partial uint NtQuerySystemInformation(int infoClass, void* buffer, uint length, out uint returnLength);

    public const int ProcessCommandLineInformation = 60;

    [LibraryImport("ntdll.dll")]
    public static unsafe partial uint NtQueryInformationProcess(nint process, int infoClass, void* buffer, uint length, out uint returnLength);

    // SYSTEM_PROCESS_INFORMATION (x64). Only the fields we read; offsets are fixed by the ABI.
    [StructLayout(LayoutKind.Explicit)]
    public struct SystemProcessInfo
    {
        [FieldOffset(0)] public uint NextEntryOffset;
        [FieldOffset(4)] public uint NumberOfThreads;
        [FieldOffset(8)] public long WorkingSetPrivateSize;
        [FieldOffset(24)] public ulong CycleTime;
        [FieldOffset(32)] public long CreateTime;
        [FieldOffset(40)] public long UserTime;
        [FieldOffset(48)] public long KernelTime;
        [FieldOffset(56)] public ushort ImageNameLength;
        [FieldOffset(64)] public nint ImageNameBuffer;
        [FieldOffset(72)] public int BasePriority;
        [FieldOffset(80)] public nint UniqueProcessId;
        [FieldOffset(88)] public nint InheritedFromUniqueProcessId;
        [FieldOffset(96)] public uint HandleCount;
        [FieldOffset(100)] public uint SessionId;
        [FieldOffset(144)] public nuint WorkingSetSize;
        [FieldOffset(200)] public nuint PrivatePageCount;
        [FieldOffset(232)] public long ReadTransferCount;
        [FieldOffset(240)] public long WriteTransferCount;
        [FieldOffset(248)] public long OtherTransferCount;
    }
}
