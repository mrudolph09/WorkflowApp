using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Workflow.Terminal.Native;

/// <summary>kernel32 entry points needed for a pseudo-console.</summary>
// CA5392: DefaultDllImportSearchPathsAttribute is only valid on a method or an assembly, not a
// class (CS0592) - it is declared once at the assembly level instead (see AssemblyInfo.cs).
// Everything here is kernel32, so System32 is both correct and the hardened choice.
internal static class NativeMethods
{
    /// <summary>PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE.</summary>
    internal const int ProcThreadAttributePseudoConsole = 0x00020016;

    /// <summary>EXTENDED_STARTUPINFO_PRESENT.</summary>
    internal const uint ExtendedStartupInfoPresent = 0x00080000;

    /// <summary>CREATE_UNICODE_ENVIRONMENT.</summary>
    internal const uint CreateUnicodeEnvironment = 0x00000400;

    /// <summary>E_NOTIMPL, returned by CreatePseudoConsole on Windows older than 10 1809.</summary>
    internal const int ENotImpl = unchecked((int)0x80004001);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CreatePipe(
        out SafeFileHandle hReadPipe,
        out SafeFileHandle hWritePipe,
        IntPtr lpPipeAttributes,
        int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern int CreatePseudoConsole(
        Coord size,
        SafeFileHandle hInput,
        SafeFileHandle hOutput,
        uint dwFlags,
        out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern int ResizePseudoConsole(IntPtr hPC, Coord size);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList,
        int dwAttributeCount,
        int dwFlags,
        ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        uint dwFlags,
        IntPtr attribute,
        IntPtr lpValue,
        IntPtr cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool CreateProcess(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfoEx lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr hObject);
}
