using System.Runtime.InteropServices;

namespace TaskManager.Native;

internal static partial class AdvApi32
{
    public const uint SC_MANAGER_CONNECT = 0x0001;
    public const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    public const int SC_ENUM_PROCESS_INFO = 0;
    public const uint SERVICE_WIN32 = 0x30;
    public const uint SERVICE_ACTIVE = 0x1;
    public const uint SERVICE_STATE_ALL = 0x3;
    public const int ERROR_MORE_DATA = 234;

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint OpenSCManager(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseServiceHandle(nint handle);

    [LibraryImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool EnumServicesStatusEx(nint scm, int infoLevel, uint serviceType, uint serviceState,
        void* services, uint bufSize, out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, nint groupName);

    [StructLayout(LayoutKind.Sequential)]
    public struct EnumServiceStatusProcess
    {
        public nint ServiceName;
        public nint DisplayName;
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode,
            CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }
}
