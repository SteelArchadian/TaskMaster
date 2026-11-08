using System.Runtime.InteropServices;

namespace TaskManager.Native;

/// <summary>Calls for single-instance signalling, the global shortcut and the shortcut agent's message loop.</summary>
internal static unsafe partial class Win32
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SecurityAttributes
    {
        public int Length;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    public const uint EVENT_MODIFY_STATE = 0x0002;
    public const uint SYNCHRONIZE = 0x00100000;
    public const int ERROR_ALREADY_EXISTS = 183;
    public const uint WAIT_OBJECT_0 = 0;
    public const uint INFINITE = 0xFFFFFFFF;
    public const uint QS_ALLINPUT = 0x04FF;
    public const uint PM_REMOVE = 1;
    public const uint WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
    public const uint ASFW_ANY = unchecked((uint)-1);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateEventW(SecurityAttributes* attributes, int manualReset, int initialState, string name);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint OpenEventW(uint access, int inherit, string name);

    [LibraryImport("kernel32.dll")]
    public static partial int SetEvent(nint handle);

    [LibraryImport("kernel32.dll")]
    public static partial nint LocalFree(nint mem);

    [StructLayout(LayoutKind.Sequential)]
    public struct StartupInfo
    {
        public int Cb;
        public nint Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public nint Reserved2Ptr, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation
    {
        public nint Process, Thread;
        public uint ProcessId, ThreadId;
    }

    public const uint DEBUG_ONLY_THIS_PROCESS = 0x2;

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int CreateProcessW(string? application, char* commandLine, nint processAttributes, nint threadAttributes,
        int inheritHandles, uint flags, nint environment, string? directory, StartupInfo* startup, ProcessInformation* info);

    [LibraryImport("kernel32.dll")]
    public static partial int DebugSetProcessKillOnExit(int kill);

    [LibraryImport("kernel32.dll")]
    public static partial int DebugActiveProcessStop(uint pid);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out nint descriptor, nint size);

    [LibraryImport("user32.dll")]
    public static partial int AllowSetForegroundWindow(uint pid);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);

    [LibraryImport("user32.dll")]
    public static partial int UnregisterHotKey(nint hwnd, int id);

    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int vk);

    [LibraryImport("user32.dll")]
    public static partial short GetKeyState(int vk);

    [LibraryImport("user32.dll")]
    public static partial uint MsgWaitForMultipleObjects(uint count, nint* handles, int waitAll, uint milliseconds, uint wakeMask);

    [LibraryImport("user32.dll")]
    public static partial int PeekMessageW(Msg* msg, nint hwnd, uint min, uint max, uint remove);
}
