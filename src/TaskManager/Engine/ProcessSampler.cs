using System.Runtime.InteropServices;
using TaskManager.Native;

namespace TaskManager.Engine;

/// <summary>One process at one tick. Immutable so the UI thread can read it while the sampler moves on.</summary>
public sealed record ProcessSample(
    uint Pid,
    uint ParentPid,
    long CreateTime,
    string ImageName,
    uint SessionId,
    uint Threads,
    uint Handles,
    int BasePriority,
    long PrivateWorkingSet,
    long WorkingSet,
    long CommitSize,
    double CpuPercent,
    double DiskBytesPerSec,
    long CpuTime100ns,
    bool Suspended = false);

/// <summary>
/// Reads every process with a single NtQuerySystemInformation call per tick. No per-process
/// handles, no PDH, so the cost is flat no matter how many processes are running.
/// </summary>
public sealed unsafe class ProcessSampler : IDisposable
{
    private void* _buffer;
    private uint _bufferSize = 1 << 20;
    private readonly Dictionary<(uint, long), (long cpu, long io)> _previous = new();
    private long _previousStamp;
    private readonly int _cpuCount = Environment.ProcessorCount;

    /// <summary>Thread id → process id from the last sample (kernel disk events name only the thread).</summary>
    public Dictionary<uint, uint> ThreadOwners { get; private set; } = new();

    /// <summary>Total threads and handles in the last sample, for the Performance page.</summary>
    public long TotalThreads { get; private set; }
    public long TotalHandles { get; private set; }

    // SYSTEM_THREAD_INFORMATION entries (0x50 bytes) follow the 0x100-byte process entry.
    private const int ProcessEntrySize = 0x100, ThreadEntrySize = 0x50;
    private const int ThreadOffClientTid = 48, ThreadOffState = 68, ThreadOffWaitReason = 72;
    private const uint StateWaiting = 5, WaitSuspended = 5;

    public ProcessSampler()
    {
        _buffer = NativeMemory.Alloc(_bufferSize);
    }

    public List<ProcessSample> Sample()
    {
        Fill();
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double elapsed100ns = _previousStamp == 0 ? 0
            : (now - _previousStamp) * 10_000_000.0 / System.Diagnostics.Stopwatch.Frequency;
        _previousStamp = now;

        var result = new List<ProcessSample>(700);
        var seen = new Dictionary<(uint, long), (long, long)>(700);
        var owners = new Dictionary<uint, uint>(ThreadOwners.Count + 256);
        long threads = 0, handles = 0;
        byte* p = (byte*)_buffer;
        while (true)
        {
            var info = (NtDll.SystemProcessInfo*)p;
            uint pid = (uint)info->UniqueProcessId;
            long cpu = info->UserTime + info->KernelTime;
            long io = info->ReadTransferCount + info->WriteTransferCount;
            var key = (pid, info->CreateTime);
            seen[key] = (cpu, io);

            double cpuPct = 0, disk = 0;
            if (elapsed100ns > 0 && _previous.TryGetValue(key, out var prev))
            {
                cpuPct = Math.Clamp((cpu - prev.cpu) / (elapsed100ns * _cpuCount) * 100.0, 0, 100);
                disk = Math.Max(0, (io - prev.io) / (elapsed100ns / 10_000_000.0));
            }

            // Suspended = every thread waiting with reason Suspended, as Task Manager's Status column shows.
            uint n = info->NumberOfThreads;
            bool suspended = n > 0 && pid > 4;
            byte* t = p + ProcessEntrySize;
            for (uint i = 0; i < n; i++, t += ThreadEntrySize)
            {
                owners[(uint)*(nint*)(t + ThreadOffClientTid)] = pid;
                if (suspended && (*(uint*)(t + ThreadOffState) != StateWaiting || *(uint*)(t + ThreadOffWaitReason) != WaitSuspended))
                    suspended = false;
            }
            threads += n;
            handles += info->HandleCount;

            string name = pid switch
            {
                0 => "System Idle Process",
                _ when info->ImageNameBuffer == 0 => "System",
                _ => new string((char*)info->ImageNameBuffer, 0, info->ImageNameLength / 2),
            };

            result.Add(new ProcessSample(
                pid,
                (uint)info->InheritedFromUniqueProcessId,
                info->CreateTime,
                name,
                info->SessionId,
                info->NumberOfThreads,
                info->HandleCount,
                info->BasePriority,
                info->WorkingSetPrivateSize,
                (long)info->WorkingSetSize,
                (long)info->PrivatePageCount,
                cpuPct,
                disk,
                cpu,
                suspended));

            if (info->NextEntryOffset == 0) break;
            p += info->NextEntryOffset;
        }

        _previous.Clear();
        foreach (var kv in seen) _previous[kv.Key] = kv.Value;
        ThreadOwners = owners;
        TotalThreads = threads;
        TotalHandles = handles;
        return result;
    }

    private void Fill()
    {
        while (true)
        {
            uint status = NtDll.NtQuerySystemInformation(NtDll.SystemProcessInformation, _buffer, _bufferSize, out uint needed);
            if (status == 0) return;
            if (status != NtDll.STATUS_INFO_LENGTH_MISMATCH)
                throw new InvalidOperationException($"NtQuerySystemInformation failed: 0x{status:X8}");
            // Processes can appear between calls, so leave headroom.
            _bufferSize = Math.Max(_bufferSize * 2, needed + (needed / 4));
            NativeMemory.Free(_buffer);
            _buffer = NativeMemory.Alloc(_bufferSize);
        }
    }

    public void Dispose()
    {
        if (_buffer != null)
        {
            NativeMemory.Free(_buffer);
            _buffer = null;
        }
    }
}
