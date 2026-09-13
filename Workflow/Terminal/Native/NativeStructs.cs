using System.Runtime.InteropServices;

namespace Workflow.Terminal.Native;

/// <summary>Windows COORD structure.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Coord
{
    /// <summary>Column count.</summary>
    public short X;

    /// <summary>Row count.</summary>
    public short Y;
}

/// <summary>Windows STARTUPINFOW structure.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct StartupInfo
{
    public int cb;
    public IntPtr lpReserved;
    public IntPtr lpDesktop;
    public IntPtr lpTitle;
    public int dwX;
    public int dwY;
    public int dwXSize;
    public int dwYSize;
    public int dwXCountChars;
    public int dwYCountChars;
    public int dwFillAttribute;
    public int dwFlags;
    public short wShowWindow;
    public short cbReserved2;
    public IntPtr lpReserved2;
    public IntPtr hStdInput;
    public IntPtr hStdOutput;
    public IntPtr hStdError;
}

/// <summary>Windows STARTUPINFOEXW structure.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct StartupInfoEx
{
    public StartupInfo StartupInfo;
    public IntPtr lpAttributeList;
}

/// <summary>Windows PROCESS_INFORMATION structure.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ProcessInformation
{
    public IntPtr hProcess;
    public IntPtr hThread;
    public int dwProcessId;
    public int dwThreadId;
}
