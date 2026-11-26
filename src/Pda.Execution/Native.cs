using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pda.Execution;

internal static class Native
{
    internal const uint CreateSuspended = 4;
    internal const uint ExtendedStartupInfoPresent = 0x80000;
    internal const uint CreateUnicodeEnvironment = 0x400;
    internal const uint CreateNoWindow = 0x8000000;
    internal const int JobExtendedLimitInformation = 9;
    internal const int JobBasicAccountingInformation = 1;
    internal const uint KillOnJobClose = 0x2000;
    internal const int StartfUseStdHandles = 0x100;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Startup
    {
        internal int Size;
        internal nint Reserved, Desktop, Title;
        internal uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        internal ushort Show, ReservedSize;
        internal nint ReservedData, Input, Output, Error, Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Child
    {
        internal nint Process, Thread;
        internal int Pid, Tid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BasicLimits
    {
        internal long ProcessTime, JobTime;
        internal uint Flags;
        internal nuint MinWorkingSet, MaxWorkingSet;
        internal uint ActiveLimit;
        internal nuint Affinity;
        internal uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Limits
    {
        internal BasicLimits Basic;
        internal ulong ReadOps, WriteOps, OtherOps;
        internal ulong ReadBytes, WriteBytes, OtherBytes;
        internal nuint ProcessMemory, JobMemory, PeakProcess, PeakJob;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Accounting
    {
        internal long User, Kernel, PeriodUser, PeriodKernel;
        internal uint Faults, Processes, Active, Terminated;
    }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern SafeFileHandle CreateJobObjectW(nint security,
        string name);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern SafeFileHandle OpenJobObjectW(uint access,
        bool inherit, string name);

    [DllImport("kernel32", SetLastError = true)]
    internal static extern bool SetInformationJobObject(SafeFileHandle job,
        int info, ref Limits limits, int size);

    [DllImport("kernel32", SetLastError = true)]
    internal static extern bool QueryInformationJobObject(SafeFileHandle job,
        int info, out Accounting result, int size, nint length);

    [DllImport("kernel32", SetLastError = true)]
    internal static extern bool TerminateJobObject(SafeFileHandle job,
        uint exitCode);

    [DllImport("kernel32", SetLastError = true)]
    internal static extern bool InitializeProcThreadAttributeList(nint list,
        int count, int flags, ref nuint bytes);

    [DllImport("kernel32", SetLastError = true)]
    internal static extern bool UpdateProcThreadAttribute(nint list,
        uint flags, nuint attribute, nint value, nuint size, nint previous,
        nint returned);

    [DllImport("kernel32")]
    internal static extern void DeleteProcThreadAttributeList(nint list);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool CreateProcessW(string application,
        StringBuilder command, nint processAttributes, nint threadAttributes,
        bool inherit, uint flags, nint environment, string directory,
        ref Startup startup, out Child process);

    [DllImport("kernel32", SetLastError = true)]
    internal static extern uint ResumeThread(SafeFileHandle thread);

    [DllImport("kernel32", SetLastError = true)]
    internal static extern bool GetProcessTimes(SafeFileHandle process,
        out long created, out long exited, out long kernel, out long user);

    [DllImport("kernel32", SetLastError = true)]
    internal static extern bool GetExitCodeProcess(SafeFileHandle process,
        out int code);

    [DllImport("kernel32", SetLastError = true)]
    internal static extern uint WaitForSingleObject(SafeFileHandle handle,
        uint milliseconds);

    internal static void Check(bool ok)
    {
        if (!ok) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    internal static uint Active(SafeFileHandle job)
    {
        Check(QueryInformationJobObject(job, JobBasicAccountingInformation,
            out var info, Marshal.SizeOf<Accounting>(), 0));
        return info.Active;
    }
}
