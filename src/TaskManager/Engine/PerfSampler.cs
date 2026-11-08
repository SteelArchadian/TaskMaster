using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using TaskManager.Native;

namespace TaskManager.Engine;

public sealed record DiskSample(
    int Number, string Name, string Model, string Kind, string Letters, long Capacity,
    double ActivePercent, double ReadBytesPerSec, double WriteBytesPerSec, double ResponseMs);

public sealed record AdapterSample(
    string Id, string Name, string Description, string Kind, double SendBitsPerSec, double ReceiveBitsPerSec,
    long LinkSpeed, string Ipv4, string Ipv6);

public sealed record GpuInfo(string Name, long DedicatedTotal, long DedicatedUsed, long SharedTotal, long SharedUsed, string Driver, string Luid,
    string DriverDate = "", string Location = "");

public sealed record MemoryDetail(
    ulong Total, ulong Available, ulong Committed, ulong CommitLimit, ulong Cached, ulong PagedPool, ulong NonPagedPool,
    ulong Installed, ulong Compressed);

public sealed record PerfSample(
    double[] CoreCpu, double SpeedGhz, MemoryDetail Memory, IReadOnlyList<DiskSample> Disks,
    IReadOnlyList<AdapterSample> Adapters, IReadOnlyList<GpuInfo> Gpus, long Threads, long Handles, TimeSpan Uptime);

/// <summary>Static facts for the Performance page, read once.</summary>
public sealed record CpuStatic(string Name, double BaseGhz, int Sockets, int Cores, int Logical, string Virtualization,
    long L1, long L2, long L3);

/// <summary>Whole-machine numbers for the Performance page: per-core CPU, memory detail, disks, adapters, GPU memory.</summary>
public sealed unsafe partial class PerfSampler : IDisposable
{
    private long[] _coreIdle = [], _coreTotal = [];
    private readonly Dictionary<int, (long idle, long query, long read, long write, long rt, long wt, long rc, long wc)> _diskPrev = new();
    private readonly Dictionary<string, (long sent, long recv, long stamp)> _netPrev = new();
    private List<(int number, SafeFileHandle handle, string model, string kind, long size)>? _disks;
    private List<NetworkInterface> _nics = new();
    private int _tick;
    private Dictionary<int, string> _letters = new();
    private List<(string name, long dedicated, long shared, string driver, string luidKey, string date, string location)> _gpuRegistry = new();
    private nint _gpuQuery, _gpuDedicated, _gpuShared;

    public static CpuStatic Cpu { get; } = ReadCpu();

    public PerfSample Sample(long threads, long handles)
    {
        _tick++;
        double[] cores = SampleCores();
        double speed = SampleSpeed();
        var mem = SampleMemory();
        var disks = Safe(SampleDisks, "disks") ?? [];
        var nets = Safe(SampleNetwork, "network") ?? [];
        var gpus = Safe(SampleGpus, "gpu memory") ?? [];
        return new PerfSample(cores, speed, mem, disks, nets, gpus, threads, handles, TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    private static T? Safe<T>(Func<T> f, string what) where T : class
    {
        try { return f(); }
        catch (Exception ex) { Log.Error(what, ex); return null; }
    }

    // ---------- CPU ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorPerformance { public long Idle, Kernel, User, Dpc, Interrupt; public uint InterruptCount, pad; }

    [LibraryImport("ntdll.dll")]
    private static partial uint NtQuerySystemInformation(int infoClass, void* buffer, uint length, out uint returnLength);

    private double[] SampleCores()
    {
        int n = Environment.ProcessorCount;
        var buf = stackalloc ProcessorPerformance[n];
        if (NtQuerySystemInformation(8, buf, (uint)(sizeof(ProcessorPerformance) * n), out _) != 0) return new double[n];
        if (_coreIdle.Length != n) { _coreIdle = new long[n]; _coreTotal = new long[n]; }
        var result = new double[n];
        for (int i = 0; i < n; i++)
        {
            long total = buf[i].Kernel + buf[i].User; // kernel time includes idle
            long dTotal = total - _coreTotal[i], dIdle = buf[i].Idle - _coreIdle[i];
            if (_coreTotal[i] != 0 && dTotal > 0) result[i] = Math.Clamp((dTotal - dIdle) * 100.0 / dTotal, 0, 100);
            _coreTotal[i] = total;
            _coreIdle[i] = buf[i].Idle;
        }
        return result;
    }

    // "% Processor Performance" × base speed is what Task Manager shows as Speed; the power API gives the same number.
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorPowerInformation { public uint Number, MaxMhz, CurrentMhz, MhzLimit, MaxIdleState, CurrentIdleState; }

    [LibraryImport("powrprof.dll")]
    private static partial int CallNtPowerInformation(int level, void* input, uint inputLength, void* output, uint outputLength);

    private nint _perfQuery, _perfCounter;

    private double SampleSpeed()
    {
        // Prefer the PDH counter (it shows turbo); fall back to the power API's current MHz.
        if (_perfQuery == 0 && _tick < 3)
        {
            if (PdhOpenQueryW(null, 0, out _perfQuery) == 0
                && PdhAddEnglishCounterW(_perfQuery, @"\Processor Information(_Total)\% Processor Performance", 0, out _perfCounter) != 0)
            { PdhCloseQuery(_perfQuery); _perfQuery = 0; }
        }
        if (_perfQuery != 0 && PdhCollectQueryData(_perfQuery) == 0
            && PdhGetFormattedCounterValue(_perfCounter, PDH_FMT_DOUBLE, null, out var v) == 0 && v.CStatus == 0 && v.Value > 0)
            return Cpu.BaseGhz * v.Value / 100.0;

        int n = Environment.ProcessorCount;
        var info = stackalloc ProcessorPowerInformation[n];
        if (CallNtPowerInformation(11, null, 0, info, (uint)(sizeof(ProcessorPowerInformation) * n)) != 0) return Cpu.BaseGhz;
        double sum = 0;
        for (int i = 0; i < n; i++) sum += info[i].CurrentMhz;
        return sum / n / 1000.0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LogicalProcessorHeader { public int Relationship; public int Size; }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLogicalProcessorInformationEx(int relationship, byte* buffer, ref uint length);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessorFeaturePresent(uint feature);

    private static CpuStatic ReadCpu()
    {
        string name = "CPU";
        double baseGhz = 0;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            name = (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? name;
            if (k?.GetValue("~MHz") is int mhz) baseGhz = mhz / 1000.0;
        }
        catch (Exception ex) { Log.Error("cpu name", ex); }

        int cores = 0, sockets = 0;
        long l1 = 0, l2 = 0, l3 = 0;
        uint len = 0;
        GetLogicalProcessorInformationEx(0xFFFF, null, ref len);
        if (len > 0)
        {
            byte* buf = (byte*)NativeMemory.Alloc(len);
            try
            {
                if (GetLogicalProcessorInformationEx(0xFFFF, buf, ref len))
                {
                    for (uint off = 0; off < len;)
                    {
                        var h = (LogicalProcessorHeader*)(buf + off);
                        if (h->Size <= 0) break;
                        if (h->Relationship == 0) cores++;
                        else if (h->Relationship == 3) sockets++;
                        else if (h->Relationship == 2)
                        {
                            // CACHE_RELATIONSHIP: Level u8 @8, CacheSize u32 @12, Type u32 @16 (2 = instruction... counted in L1).
                            byte level = buf[off + 8];
                            long size = *(uint*)(buf + off + 12);
                            if (level == 1) l1 += size; else if (level == 2) l2 += size; else if (level == 3) l3 += size;
                        }
                        off += (uint)h->Size;
                    }
                }
            }
            finally { NativeMemory.Free(buf); }
        }
        // PF_VIRT_FIRMWARE_ENABLED
        string virt = IsProcessorFeaturePresent(21) ? "Enabled" : "Disabled";
        return new CpuStatic(name, baseGhz, Math.Max(1, sockets), Math.Max(1, cores), Environment.ProcessorCount, virt, l1, l2, l3);
    }

    // ---------- Memory ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint Size;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache,
            KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [LibraryImport("psapi.dll", EntryPoint = "GetPerformanceInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPerformanceInfo(PerformanceInformation* info, uint size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPhysicallyInstalledSystemMemory(out ulong kilobytes);

    public long CompressedBytes { get; set; }

    private MemoryDetail SampleMemory()
    {
        var pi = new PerformanceInformation { Size = (uint)sizeof(PerformanceInformation) };
        GetPerformanceInfo(&pi, pi.Size);
        ulong page = pi.PageSize;
        ulong installed = GetPhysicallyInstalledSystemMemory(out ulong kb) ? kb * 1024 : pi.PhysicalTotal * page;
        return new MemoryDetail(
            pi.PhysicalTotal * page, pi.PhysicalAvailable * page, pi.CommitTotal * page, pi.CommitLimit * page,
            pi.SystemCache * page, pi.KernelPaged * page, pi.KernelNonpaged * page, installed, (ulong)CompressedBytes);
    }

    /// <summary>SMBIOS memory devices: (speed MT/s, slots used, slots total, form factor).</summary>
    public static (int speed, int used, int total, string form) MemoryModules { get; } = ReadSmbiosMemory();

    [LibraryImport("kernel32.dll")]
    private static partial uint GetSystemFirmwareTable(uint provider, uint id, byte* buffer, uint size);

    private static (int, int, int, string) ReadSmbiosMemory()
    {
        try
        {
            const uint RSMB = 0x52534D42;
            uint size = GetSystemFirmwareTable(RSMB, 0, null, 0);
            if (size == 0) return (0, 0, 0, "");
            byte* buf = (byte*)NativeMemory.Alloc(size);
            try
            {
                if (GetSystemFirmwareTable(RSMB, 0, buf, size) != size) return (0, 0, 0, "");
                // RawSMBIOSData: 8-byte header, then the structure table.
                uint tableLen = *(uint*)(buf + 4);
                byte* p = buf + 8, end = p + Math.Min(tableLen, size - 8);
                int speed = 0, used = 0, total = 0; byte form = 0;
                while (p + 4 <= end)
                {
                    byte type = p[0], len = p[1];
                    if (len < 4) break;
                    if (type == 17)
                    {
                        total++;
                        ushort sz = len > 0x0D ? *(ushort*)(p + 0x0C) : (ushort)0;
                        if (sz != 0 && sz != 0xFFFF)
                        {
                            used++;
                            if (len > 0x0E) form = p[0x0E];
                            int s = len > 0x21 ? *(ushort*)(p + 0x20) : 0;
                            if (s == 0 && len > 0x16) s = *(ushort*)(p + 0x15);
                            speed = Math.Max(speed, s);
                        }
                    }
                    if (type == 127) break;
                    // Skip the formatted area, then the string set (ends with a double NUL).
                    byte* s2 = p + len;
                    while (s2 + 1 < end && !(s2[0] == 0 && s2[1] == 0)) s2++;
                    p = s2 + 2;
                }
                string ff = form switch { 0x09 => "DIMM", 0x0D => "SODIMM", 0x0F => "FB-DIMM", 0 => "", _ => "Other" };
                return (speed, used, total, ff);
            }
            finally { NativeMemory.Free(buf); }
        }
        catch (Exception ex) { Log.Error("smbios", ex); return (0, 0, 0, ""); }
    }

    // ---------- Disks ----------

    private const uint IOCTL_DISK_PERFORMANCE = 0x70020;
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;
    private const uint IOCTL_DISK_GET_LENGTH_INFO = 0x7405C;
    private const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x700A0;
    private const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x2D1080;

    [StructLayout(LayoutKind.Sequential)]
    private struct DiskPerformance
    {
        public long BytesRead, BytesWritten, ReadTime, WriteTime, IdleTime;
        public uint ReadCount, WriteCount, QueueDepth, SplitCount;
        public long QueryTime;
        public uint StorageDeviceNumber;
        public fixed char StorageManagerName[8];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, void* inBuf, uint inSize, void* outBuf, uint outSize, out uint returned, nint overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    private static SafeFileHandle OpenDevice(string path) => CreateFile(path, 0, 3 /* read|write share */, 0, 3 /* OPEN_EXISTING */, 0, 0);

    private void FindDisks()
    {
        _disks = new();
        for (int i = 0; i < 32; i++)
        {
            var h = OpenDevice($@"\\.\PhysicalDrive{i}");
            if (h.IsInvalid) { h.Dispose(); continue; }
            string model = "", kind = "";
            long size = 0;
            byte* buf = stackalloc byte[1024];
            int* query = stackalloc int[3];
            query[0] = 0; query[1] = 0; query[2] = 0; // StorageDeviceProperty, PropertyStandardQuery
            if (DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, query, 12, buf, 1024, out _, 0))
            {
                uint productOff = *(uint*)(buf + 16);
                uint busType = *(uint*)(buf + 28);
                if (productOff > 0 && productOff < 1024) model = Marshal.PtrToStringAnsi((nint)(buf + productOff))?.Trim() ?? "";
                kind = busType switch { 17 => "SSD (NVMe)", 7 => "Removable (USB)", _ => "" };
            }
            if (kind.Length == 0)
            {
                // StorageDeviceSeekPenaltyProperty: IncursSeekPenalty byte @8.
                query[0] = 7;
                byte* sp = stackalloc byte[12];
                if (DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, query, 12, sp, 12, out _, 0))
                    kind = sp[8] != 0 ? "HDD" : "SSD";
            }
            long len;
            if (DeviceIoControl(h, IOCTL_DISK_GET_LENGTH_INFO, null, 0, &len, 8, out _, 0)) size = len;
            else
            {
                // Without read access (not elevated) the length query fails; the geometry query needs none. DiskSize is at offset 24.
                byte* geo = stackalloc byte[256];
                if (DeviceIoControl(h, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, null, 0, geo, 256, out _, 0)) size = *(long*)(geo + 24);
            }
            _disks.Add((i, h, model, kind, size));
        }
        _letters = new();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                using var v = OpenDevice($@"\\.\{d.Name.TrimEnd('\\')}");
                if (v.IsInvalid) continue;
                uint* num = stackalloc uint[3];
                if (DeviceIoControl(v, IOCTL_STORAGE_GET_DEVICE_NUMBER, null, 0, num, 12, out _, 0))
                    _letters[(int)num[1]] = (_letters.TryGetValue((int)num[1], out var l) ? l + " " : "") + d.Name.TrimEnd('\\');
            }
            catch (Exception) { /* optical drive without media and similar */ }
        }
    }

    private List<DiskSample> SampleDisks()
    {
        if (_disks == null || _tick % 30 == 0) { DisposeDisks(); FindDisks(); }
        var list = new List<DiskSample>();
        foreach (var (number, h, model, kind, size) in _disks!)
        {
            DiskPerformance dp;
            if (!DeviceIoControl(h, IOCTL_DISK_PERFORMANCE, null, 0, &dp, (uint)sizeof(DiskPerformance), out _, 0)) continue;
            double active = 0, read = 0, write = 0, resp = 0;
            if (_diskPrev.TryGetValue(number, out var p) && dp.QueryTime > p.query)
            {
                double dq = dp.QueryTime - p.query;
                active = Math.Clamp(100 - (dp.IdleTime - p.idle) * 100.0 / dq, 0, 100);
                double secs = dq / 10_000_000.0;
                read = (dp.BytesRead - p.read) / secs;
                write = (dp.BytesWritten - p.write) / secs;
                long ops = dp.ReadCount - p.rc + dp.WriteCount - p.wc;
                if (ops > 0) resp = (dp.ReadTime - p.rt + dp.WriteTime - p.wt) / 10_000.0 / ops;
            }
            _diskPrev[number] = (dp.IdleTime, dp.QueryTime, dp.BytesRead, dp.BytesWritten, dp.ReadTime, dp.WriteTime, dp.ReadCount, dp.WriteCount);
            string letters = _letters.TryGetValue(number, out var l) ? l : "";
            list.Add(new DiskSample(number, $"Disk {number}" + (letters.Length > 0 ? $" ({letters})" : ""), model, kind, letters, size,
                active, Math.Max(0, read), Math.Max(0, write), resp));
        }
        return list;
    }

    private void DisposeDisks()
    {
        if (_disks == null) return;
        foreach (var d in _disks) d.handle.Dispose();
        _disks = null;
    }

    // ---------- Network ----------

    private List<AdapterSample> SampleNetwork()
    {
        if (_tick % 10 == 1 || _nics.Count == 0)
        {
            _nics = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    && !n.Description.Contains("Filter", StringComparison.OrdinalIgnoreCase))
                .ToList();
            // Filter drivers bound to an adapter show up as "<adapter>-<filter>-0000" copies of it; Task Manager lists the adapter once.
            var names = _nics.Select(n => n.Name).ToList();
            _nics.RemoveAll(n => names.Any(b => b != n.Name && n.Name.StartsWith(b + "-", StringComparison.Ordinal)));
        }
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        var list = new List<AdapterSample>();
        foreach (var n in _nics)
        {
            IPInterfaceStatistics st;
            try { st = n.GetIPStatistics(); } catch (NetworkInformationException) { continue; }
            double sendBps = 0, recvBps = 0;
            if (_netPrev.TryGetValue(n.Id, out var p))
            {
                double secs = (now - p.stamp) / (double)System.Diagnostics.Stopwatch.Frequency;
                if (secs > 0)
                {
                    sendBps = Math.Max(0, (st.BytesSent - p.sent) * 8 / secs);
                    recvBps = Math.Max(0, (st.BytesReceived - p.recv) * 8 / secs);
                }
            }
            _netPrev[n.Id] = (st.BytesSent, st.BytesReceived, now);
            string kind = n.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "Ethernet",
                NetworkInterfaceType.Ppp => "Cellular",
                _ => "Ethernet",
            };
            string v4 = "", v6 = "";
            if (_tick % 10 == 1 || !_addr.TryGetValue(n.Id, out var a))
            {
                try
                {
                    foreach (var u in n.GetIPProperties().UnicastAddresses)
                    {
                        if (u.Address.AddressFamily == AddressFamily.InterNetwork && v4.Length == 0) v4 = u.Address.ToString();
                        else if (u.Address.AddressFamily == AddressFamily.InterNetworkV6 && v6.Length == 0) v6 = u.Address.ToString();
                    }
                }
                catch (NetworkInformationException) { }
                _addr[n.Id] = (v4, v6);
            }
            else (v4, v6) = a;
            list.Add(new AdapterSample(n.Id, n.Name, n.Description, kind, sendBps, recvBps, n.Speed, v4, v6));
        }
        return list;
    }

    private readonly Dictionary<string, (string, string)> _addr = new();

    // ---------- GPU memory ----------

    private const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_LARGE = 0x400;

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhValue { public uint CStatus; public uint pad; public double Value; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhItem { public char* Name; public uint CStatus; public uint pad; public long Value; }

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQueryW(string? source, nint user, out nint query);

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounterW(nint query, string path, nint user, out nint counter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhGetFormattedCounterValue(nint counter, uint format, uint* type, out PdhValue value);

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static partial uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint size, out uint count, void* items);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(nint query);

    private List<GpuInfo> SampleGpus()
    {
        if (_tick == 1) ReadGpuRegistry();
        if (_gpuRegistry.Count == 0) return [];
        if (_gpuQuery == 0 && _tick < 3)
        {
            if (PdhOpenQueryW(null, 0, out _gpuQuery) == 0)
            {
                PdhAddEnglishCounterW(_gpuQuery, @"\GPU Adapter Memory(*)\Dedicated Usage", 0, out _gpuDedicated);
                PdhAddEnglishCounterW(_gpuQuery, @"\GPU Adapter Memory(*)\Shared Usage", 0, out _gpuShared);
            }
        }
        var ded = new List<(string, long)>();
        var sha = new List<(string, long)>();
        if (_gpuQuery != 0 && PdhCollectQueryData(_gpuQuery) == 0)
        {
            ded = ReadArray(_gpuDedicated);
            sha = ReadArray(_gpuShared);
        }
        var list = new List<GpuInfo>();
        // PDH instance names are "luid_0x00000000_0x0000D1C3_phys_0", matched to each adapter by its LUID.
        foreach (var r in _gpuRegistry)
        {
            long dedUsed = ded.Where(d => LuidOf(d.Item1).Equals(r.luidKey, StringComparison.OrdinalIgnoreCase)).Sum(d => d.Item2);
            long shaUsed = sha.Where(d => LuidOf(d.Item1).Equals(r.luidKey, StringComparison.OrdinalIgnoreCase)).Sum(d => d.Item2);
            list.Add(new GpuInfo(r.name, r.dedicated, dedUsed, r.shared, shaUsed, r.driver, r.luidKey, r.date, r.location));
        }
        return list;
    }

    private static string LuidOf(string instance)
    {
        int p = instance.IndexOf("_phys", StringComparison.Ordinal);
        return p > 0 ? instance[..p] : instance;
    }

    private static List<(string, long)> ReadArray(nint counter)
    {
        var list = new List<(string, long)>();
        if (counter == 0) return list;
        uint size = 0;
        PdhGetFormattedCounterArray(counter, PDH_FMT_LARGE, ref size, out _, null);
        if (size == 0) return list;
        byte* buf = (byte*)NativeMemory.Alloc(size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, PDH_FMT_LARGE, ref size, out uint count, buf) != 0) return list;
            var items = (PdhItem*)buf;
            for (int i = 0; i < count; i++)
                if (items[i].CStatus == 0) list.Add((new string(items[i].Name), items[i].Value));
        }
        finally { NativeMemory.Free(buf); }
        return list;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SegmentSizeInfo { public ulong DedicatedVideo, DedicatedSystem, SharedSystem; }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterAddress { public uint Bus, Device, Function; }

    private const int KMTQAITYPE_GETSEGMENTSIZE = 3, KMTQAITYPE_ADAPTERADDRESS = 6, KMTQAITYPE_ADAPTERREGISTRYINFO = 8;

    /// <summary>Hardware render adapters from the graphics kernel: name, memory sizes (what Task Manager shows) and LUID.</summary>
    private void ReadGpuRegistry()
    {
        _gpuRegistry = new();
        try
        {
            var drivers = new Dictionary<string, (string ver, string date)>(StringComparer.OrdinalIgnoreCase);
            using (var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
            {
                foreach (var sub in cls?.GetSubKeyNames().Where(n => n.Length == 4 && char.IsDigit(n[0])) ?? [])
                {
                    using var k = cls!.OpenSubKey(sub);
                    if (k?.GetValue("DriverDesc") is string desc && k.GetValue("DriverVersion") is string ver)
                    {
                        // Stored as "9-23-2025"; Task Manager shows it as a short date.
                        string date = DateTime.TryParseExact(k.GetValue("DriverDate") as string, "M-d-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
                            ? dt.ToShortDateString() : "";
                        drivers.TryAdd(desc, (ver, date));
                    }
                }
            }

            var e = new D3dkmt.EnumAdapters2();
            if (D3dkmt.D3DKMTEnumAdapters2(&e) != 0 || e.NumAdapters == 0) return;
            var infos = new D3dkmt.AdapterInfo[e.NumAdapters];
            fixed (D3dkmt.AdapterInfo* p = infos)
            {
                e.Adapters = p;
                if (D3dkmt.D3DKMTEnumAdapters2(&e) != 0) return;
            }
            var seen = new HashSet<string>();
            foreach (var info in infos.Take((int)e.NumAdapters))
            {
                try
                {
                    uint type = 0;
                    var q = new D3dkmt.QueryAdapterInfo { hAdapter = info.hAdapter, Type = D3dkmt.KMTQAITYPE_ADAPTERTYPE, PrivateData = &type, PrivateDataSize = 4 };
                    if (D3dkmt.D3DKMTQueryAdapterInfo(&q) != 0 || (type & 0x4) != 0 || (type & 0x1) == 0) continue;
                    string luid = $"luid_0x{info.LuidHigh:X8}_0x{info.LuidLow:X8}";
                    if (!seen.Add(luid)) continue;

                    char* reg = stackalloc char[4 * 260];
                    q = new D3dkmt.QueryAdapterInfo { hAdapter = info.hAdapter, Type = KMTQAITYPE_ADAPTERREGISTRYINFO, PrivateData = reg, PrivateDataSize = 4 * 260 * 2 };
                    string name = D3dkmt.D3DKMTQueryAdapterInfo(&q) == 0 ? new string(reg).Trim() : "";
                    var seg = new SegmentSizeInfo();
                    q = new D3dkmt.QueryAdapterInfo { hAdapter = info.hAdapter, Type = KMTQAITYPE_GETSEGMENTSIZE, PrivateData = &seg, PrivateDataSize = (uint)sizeof(SegmentSizeInfo) };
                    D3dkmt.D3DKMTQueryAdapterInfo(&q);
                    var addr = new AdapterAddress();
                    q = new D3dkmt.QueryAdapterInfo { hAdapter = info.hAdapter, Type = KMTQAITYPE_ADAPTERADDRESS, PrivateData = &addr, PrivateDataSize = (uint)sizeof(AdapterAddress) };
                    string location = D3dkmt.D3DKMTQueryAdapterInfo(&q) == 0 && addr.Bus != uint.MaxValue
                        ? $"PCI bus {addr.Bus}, device {addr.Device}, function {addr.Function}" : "";
                    if (name.Length == 0) name = "GPU";
                    var drv = drivers.GetValueOrDefault(name);
                    _gpuRegistry.Add((name, (long)seg.DedicatedVideo, (long)seg.SharedSystem, drv.ver ?? "", luid, drv.date ?? "", location));
                }
                finally
                {
                    uint h = info.hAdapter;
                    D3dkmt.D3DKMTCloseAdapter(&h);
                }
            }
        }
        catch (Exception ex) { Log.Error("gpu adapters", ex); }
    }

    public void Dispose()
    {
        DisposeDisks();
        if (_gpuQuery != 0) PdhCloseQuery(_gpuQuery);
        if (_perfQuery != 0) PdhCloseQuery(_perfQuery);
    }
}
