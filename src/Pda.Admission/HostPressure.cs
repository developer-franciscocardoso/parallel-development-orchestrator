using System.Runtime.InteropServices;

namespace Pda.Admission;

public sealed record HostPressure(double CpuPercent, double DiskPercent,
    long AvailableMemoryBytes, int ProcessCount)
{
    private const long MiB = 1024 * 1024;

    public int Capacity(int limit)
    {
        if (!double.IsFinite(CpuPercent) || !double.IsFinite(DiskPercent) ||
            CpuPercent is < 0 or >= 95 || DiskPercent is < 0 or >= 95 ||
            AvailableMemoryBytes < 512 * MiB || ProcessCount is <= 0 or >= 2048)
            return 0;
        var memory = (int)Math.Min(limit, AvailableMemoryBytes / (512 * MiB));
        if (CpuPercent >= 75 || DiskPercent >= 75 || ProcessCount >= 1024)
            return Math.Min(memory, 1);
        return memory;
    }

    public static HostPressure Measure()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows counters required.");
        if (PdhOpenQuery(null, 0, out var query) != 0) return Unavailable;
        try
        {
            if (!Add(query, @"\Processor(_Total)\% Processor Time", out var cpu)
                || !Add(query, @"\PhysicalDisk(_Total)\% Idle Time", out var disk)
                || !Add(query, @"\Memory\Available MBytes", out var memory)
                || !Add(query, @"\System\Processes", out var count)
                || PdhCollectQueryData(query) != 0) return Unavailable;
            Thread.Sleep(200);
            if (PdhCollectQueryData(query) != 0) return Unavailable;
            var idle = Read(disk);
            return new(Read(cpu), Math.Clamp(100 - idle, 0, 100),
                (long)(Read(memory) * MiB), (int)Read(count));
        }
        finally { PdhCloseQuery(query); }
    }

    private static HostPressure Unavailable => new(double.NaN, double.NaN, 0, 0);

    private static bool Add(nint query, string path, out nint counter) =>
        PdhAddEnglishCounter(query, path, 0, out counter) == 0;

    private static double Read(nint counter)
    {
        var result = PdhGetFormattedCounterValue(counter, 0x200, 0,
            out var value);
        return result == 0 && value.Status <= 1 ? value.Number : double.NaN;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct CounterValue
    {
        [FieldOffset(0)] internal uint Status;
        [FieldOffset(8)] internal double Number;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? source, nint user,
        out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(nint query, string path,
        nint user, out nint counter);
    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(nint counter,
        uint format, nint type, out CounterValue value);
    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(nint query);
}
