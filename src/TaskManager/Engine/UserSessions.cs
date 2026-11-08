using System.Runtime.InteropServices;

namespace TaskManager.Engine;

public sealed record UserSession(uint Id, string User, string Domain, string Station, bool Disconnected);

/// <summary>Signed-in sessions through the Terminal Services API, the same source Task Manager's Users page uses.</summary>
public static unsafe partial class UserSessions
{
    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public uint SessionId;
        public char* WinStationName;
        public int State;
    }

    private const int WTSUserName = 5, WTSDomainName = 7, WTSDisconnected = 4;

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    private static partial int WTSEnumerateSessionsW(nint server, uint reserved, uint version, out WtsSessionInfo* info, out uint count);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    private static partial int WTSQuerySessionInformationW(nint server, uint sessionId, int infoClass, out char* buffer, out uint bytes);

    [LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(void* memory);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    private static partial int WTSDisconnectSession(nint server, uint sessionId, int wait);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    private static partial int WTSLogoffSession(nint server, uint sessionId, int wait);

    public static List<UserSession> Query()
    {
        var list = new List<UserSession>();
        if (WTSEnumerateSessionsW(0, 0, 1, out var info, out uint count) == 0) return list;
        try
        {
            for (int i = 0; i < count; i++)
            {
                var s = info[i];
                string user = Info(s.SessionId, WTSUserName);
                if (user.Length == 0) continue;
                list.Add(new UserSession(s.SessionId, user, Info(s.SessionId, WTSDomainName),
                    s.WinStationName != null ? new string(s.WinStationName) : "", s.State == WTSDisconnected));
            }
        }
        finally { WTSFreeMemory(info); }
        return list;
    }

    private static string Info(uint id, int infoClass)
    {
        if (WTSQuerySessionInformationW(0, id, infoClass, out var buf, out _) == 0) return "";
        try { return buf != null ? new string(buf) : ""; }
        finally { WTSFreeMemory(buf); }
    }

    /// <summary>Returns an error message, or null on success.</summary>
    public static string? Disconnect(uint id) =>
        WTSDisconnectSession(0, id, 0) != 0 ? null : new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()).Message;

    public static string? SignOff(uint id) =>
        WTSLogoffSession(0, id, 0) != 0 ? null : new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()).Message;
}
