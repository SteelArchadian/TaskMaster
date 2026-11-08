using System.Runtime.InteropServices;

namespace TaskManager.Native;

/// <summary>
/// The user-mode graphics kernel thunks (gdi32) that report GPU engine running time per process.
/// Same source System Informer uses; read-only, no driver, no PDH.
/// </summary>
internal static unsafe partial class D3dkmt
{
    public const int QUERYSTATISTICS_ADAPTER = 0;
    public const int QUERYSTATISTICS_PROCESS = 1;
    public const int QUERYSTATISTICS_NODE = 5;
    public const int QUERYSTATISTICS_PROCESS_NODE = 6;
    public const int KMTQAITYPE_ADAPTERTYPE = 15;

    // D3DKMT_QUERYSTATISTICS is 0x328 bytes on x64: Type, LUID, process handle, a 0x308-byte result union,
    // then the query-parameter union (NodeId) at 0x320.
    public const int QueryStatisticsSize = 0x328;
    public const int OffType = 0, OffLuid = 4, OffProcess = 16, OffResult = 24, OffQueryParam = 0x320;

    [StructLayout(LayoutKind.Sequential)]
    public struct AdapterInfo
    {
        public uint hAdapter;
        public uint LuidLow;
        public int LuidHigh;
        public uint NumOfSources;
        public int PrecisePresentRegionsPreferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EnumAdapters2
    {
        public uint NumAdapters;
        public AdapterInfo* Adapters;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct QueryAdapterInfo
    {
        public uint hAdapter;
        public int Type;
        public void* PrivateData;
        public uint PrivateDataSize;
    }

    [LibraryImport("gdi32.dll")]
    public static partial int D3DKMTEnumAdapters2(EnumAdapters2* args);

    [LibraryImport("gdi32.dll")]
    public static partial int D3DKMTQueryAdapterInfo(QueryAdapterInfo* args);

    [LibraryImport("gdi32.dll")]
    public static partial int D3DKMTOpenAdapterFromLuid(uint* args);

    [LibraryImport("gdi32.dll")]
    public static partial int D3DKMTCloseAdapter(uint* hAdapter);

    [LibraryImport("gdi32.dll")]
    public static partial int D3DKMTQueryStatistics(byte* args);
}
