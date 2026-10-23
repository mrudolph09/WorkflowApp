using System.Runtime.InteropServices;

namespace Workflow.Services.Native;

/// <summary>Windows LASTINPUTINFO structure.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct LastInputInfo
{
    public uint cbSize;
    public uint dwTime;
}

/// <summary>Windows MSG structure.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Msg
{
    public IntPtr hwnd;
    public uint message;
    public IntPtr wParam;
    public IntPtr lParam;
    public uint time;
    public int ptX;
    public int ptY;

    // Part of the documented layout: without it the struct is 4 bytes short on x86, and GetMessage
    // and PeekMessage write past the caller's buffer.
    public uint lPrivate;
}

/// <summary>Windows JOBOBJECT_BASIC_ACCOUNTING_INFORMATION structure.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct JobObjectBasicAccountingInfo
{
    public long TotalUserTime;
    public long TotalKernelTime;
    public long ThisPeriodTotalUserTime;
    public long ThisPeriodTotalKernelTime;
    public uint TotalPageFaultCount;
    public uint TotalProcesses;
    public uint ActiveProcesses;
    public uint TotalTerminatedProcesses;
}

/// <summary>Windows JOBOBJECT_BASIC_LIMIT_INFORMATION structure.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct JobObjectBasicLimitInfo
{
    public long PerProcessUserTimeLimit;
    public long PerJobUserTimeLimit;
    public uint LimitFlags;
    public UIntPtr MinimumWorkingSetSize;
    public UIntPtr MaximumWorkingSetSize;
    public uint ActiveProcessLimit;
    public UIntPtr Affinity;
    public uint PriorityClass;
    public uint SchedulingClass;
}

/// <summary>Windows IO_COUNTERS structure.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct IoCounters
{
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
}

/// <summary>Windows JOBOBJECT_EXTENDED_LIMIT_INFORMATION structure.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct JobObjectExtendedLimitInfo
{
    public JobObjectBasicLimitInfo BasicLimitInformation;
    public IoCounters IoInfo;
    public UIntPtr ProcessMemoryLimit;
    public UIntPtr JobMemoryLimit;
    public UIntPtr PeakProcessMemoryUsed;
    public UIntPtr PeakJobMemoryUsed;
}
