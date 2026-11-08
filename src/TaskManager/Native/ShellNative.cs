using System.Runtime.InteropServices;

namespace TaskManager.Native;

/// <summary>Calls behind the Processes context menu: the shell's Properties dialog and memory dumps.</summary>
internal static unsafe partial class Win32
{
    [StructLayout(LayoutKind.Sequential)]
    public struct ShellExecuteInfo
    {
        public int Size;
        public uint Mask;
        public nint Hwnd;
        public char* Verb;
        public char* File;
        public char* Parameters;
        public char* Directory;
        public int Show;
        public nint InstApp;
        public nint IdList;
        public char* Class;
        public nint ClassKey;
        public uint HotKey;
        public nint IconOrMonitor;
        public nint Process;
    }

    public const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
    public const uint SEE_MASK_FLAG_NO_UI = 0x00000400;
    public const int SW_SHOW = 5, SW_RESTORE = 9;
    public const uint PROCESS_VM_READ = 0x0010;
    public const uint MiniDumpWithFullMemory = 0x00000002, MiniDumpWithHandleData = 0x00000004,
        MiniDumpWithUnloadedModules = 0x00000020, MiniDumpWithFullMemoryInfo = 0x00000800, MiniDumpWithThreadInfo = 0x00001000;

    [LibraryImport("shell32.dll", SetLastError = true)]
    public static partial int ShellExecuteExW(ShellExecuteInfo* info);

    [LibraryImport("dbghelp.dll", SetLastError = true)]
    public static partial int MiniDumpWriteDump(nint process, uint pid, nint file, uint type, nint exception, nint userStream, nint callback);
}
