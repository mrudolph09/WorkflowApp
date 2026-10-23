using System.Runtime.InteropServices;

namespace Workflow.Services.Native;

/// <summary>Callback of a low-level keyboard or mouse hook (LowLevelKeyboardProc / LowLevelMouseProc).</summary>
/// <param name="nCode">Negative: pass on without processing.</param>
/// <param name="wParam">The message identifier.</param>
/// <param name="lParam">Pointer to a KBDLLHOOKSTRUCT or MSLLHOOKSTRUCT.</param>
/// <returns>The result of CallNextHookEx.</returns>
internal delegate IntPtr LowLevelHookProc(int nCode, IntPtr wParam, IntPtr lParam);

/// <summary>
/// user32, kernel32 and wtsapi32 entry points needed for desktop jobs: user activity, low-level
/// input hooks, system CPU times, job objects and bringing another instance's window to the front.
/// </summary>
// CA5392 is satisfied at the assembly level (see AssemblyInfo.cs); all three DLLs live in System32.
internal static class NativeMethods
{
    /// <summary>WH_KEYBOARD_LL.</summary>
    internal const int WhKeyboardLl = 13;

    /// <summary>WH_MOUSE_LL.</summary>
    internal const int WhMouseLl = 14;

    /// <summary>WM_QUIT.</summary>
    internal const uint WmQuit = 0x0012;

    /// <summary>SW_RESTORE.</summary>
    internal const int SwRestore = 9;

    /// <summary>SPI_GETSCREENSAVERRUNNING.</summary>
    internal const uint SpiGetScreenSaverRunning = 0x0072;

    /// <summary>DESKTOP_SWITCHDESKTOP.</summary>
    internal const uint DesktopSwitchDesktop = 0x0100;

    /// <summary>WTS_CURRENT_SERVER_HANDLE.</summary>
    internal static readonly IntPtr WtsCurrentServerHandle = IntPtr.Zero;

    /// <summary>WTS_CURRENT_SESSION.</summary>
    internal const int WtsCurrentSession = -1;

    /// <summary>WTSSessionInfoEx, the WTS_INFO_CLASS value whose result carries the lock flag.</summary>
    internal const int WtsSessionInfoEx = 25;

    /// <summary>Byte offset of WTSINFOEX_LEVEL1_W.SessionFlags inside WTSINFOEXW (Level, then the 8-aligned union).</summary>
    internal const int WtsInfoExSessionFlagsOffset = 16;

    /// <summary>WTS_SESSIONSTATE_LOCK.</summary>
    internal const int WtsSessionStateLock = 0;

    /// <summary>JobObjectBasicAccountingInformation.</summary>
    internal const int JobObjectBasicAccountingInformation = 1;

    /// <summary>PM_NOREMOVE.</summary>
    internal const uint PmNoRemove = 0x0000;

    /// <summary>JobObjectExtendedLimitInformation.</summary>
    internal const int JobObjectExtendedLimitInformation = 9;

    /// <summary>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE.</summary>
    internal const uint JobObjectLimitKillOnJobClose = 0x00002000;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetLastInputInfo(ref LastInputInfo plii);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref int pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(int idHook, LowLevelHookProc lpfn, IntPtr hmod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    internal static extern bool PeekMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern SafeJobObjectHandle CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetInformationJobObject(
        SafeJobObjectHandle hJob,
        int jobObjectInfoClass,
        ref JobObjectExtendedLimitInfo lpJobObjectInfo,
        int cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool AssignProcessToJobObject(SafeJobObjectHandle hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool TerminateJobObject(SafeJobObjectHandle hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool QueryInformationJobObject(
        SafeJobObjectHandle hJob,
        int jobObjectInfoClass,
        out JobObjectBasicAccountingInfo lpJobObjectInfo,
        int cbJobObjectInfoLength,
        IntPtr lpReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    internal static extern bool WTSQuerySessionInformation(
        IntPtr hServer,
        int sessionId,
        int wtsInfoClass,
        out IntPtr ppBuffer,
        out int pBytesReturned);

    [DllImport("wtsapi32.dll")]
    internal static extern void WTSFreeMemory(IntPtr pMemory);
}
