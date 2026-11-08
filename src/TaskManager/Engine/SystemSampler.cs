using TaskManager.Native;

namespace TaskManager.Engine;

public readonly record struct SystemSample(double CpuPercent, double MemoryPercent, ulong MemoryUsed, ulong MemoryTotal);

/// <summary>Whole-machine CPU and memory totals for the column headers and the Performance page.</summary>
public sealed class SystemSampler
{
    private long _idle, _kernel, _user;

    public SystemSample Sample()
    {
        double cpu = 0;
        if (Kernel32.GetSystemTimes(out long idle, out long kernel, out long user))
        {
            long dIdle = idle - _idle, dTotal = (kernel - _kernel) + (user - _user);
            if (_kernel != 0 && dTotal > 0)
                cpu = Math.Clamp((dTotal - dIdle) * 100.0 / dTotal, 0, 100);
            _idle = idle; _kernel = kernel; _user = user;
        }

        var mem = new Kernel32.MemoryStatusEx { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Kernel32.MemoryStatusEx>() };
        Kernel32.GlobalMemoryStatusEx(ref mem);
        ulong used = mem.TotalPhys - mem.AvailPhys;
        return new SystemSample(cpu, mem.TotalPhys == 0 ? 0 : used * 100.0 / mem.TotalPhys, used, mem.TotalPhys);
    }
}
