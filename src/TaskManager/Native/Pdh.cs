using System.Runtime.InteropServices;

namespace TaskManager.Native;

/// <summary>Only used once, to read the GPU engine type names ("3D", "Copy"...) that the driver publishes.</summary>
internal static unsafe partial class Pdh
{
    public const int PDH_MORE_DATA = unchecked((int)0x800007D2);
    public const uint PERF_DETAIL_WIZARD = 400;

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int PdhEnumObjectItemsW(string? dataSource, string? machineName, string objectName,
        char* counterList, ref uint counterListLength, char* instanceList, ref uint instanceListLength,
        uint detailLevel, uint flags);
}
