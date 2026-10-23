using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Workflow.Models;
using Workflow.Services.Native;

namespace Workflow.Services;

/// <inheritdoc cref="IUserActivityMonitor" />
public sealed class Win32UserActivityMonitor : IUserActivityMonitor
{
    /// <inheritdoc />
    public UserActivity Read() => new(IdleTime(), IsLocked(), IsScreenSaverRunning());

    /// <inheritdoc />
    public IRealInputWatch StartRealInputWatch() => LowLevelInputWatch.Start();

    /// <summary>Time since the session's last input, from GetLastInputInfo.</summary>
    private static TimeSpan IdleTime()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!NativeMethods.GetLastInputInfo(ref info))
        {
            return TimeSpan.Zero;
        }

        // dwTime is a 32-bit tick count; subtracting in uint arithmetic stays right across its
        // 49.7-day wrap, as long as the idle time itself is shorter than that.
        var elapsed = unchecked((uint)Environment.TickCount64 - info.dwTime);

        return TimeSpan.FromMilliseconds(elapsed);
    }

    /// <summary>
    /// True when the session reports itself locked, or when the input desktop cannot be opened -
    /// which is the case on the lock screen and on the secure desktop alike.
    /// </summary>
    private static bool IsLocked()
    {
        if (NativeMethods.WTSQuerySessionInformation(
                NativeMethods.WtsCurrentServerHandle,
                NativeMethods.WtsCurrentSession,
                NativeMethods.WtsSessionInfoEx,
                out var buffer,
                out var bytes))
        {
            try
            {
                if (bytes >= NativeMethods.WtsInfoExSessionFlagsOffset + sizeof(int)
                    && Marshal.ReadInt32(buffer) == 1
                    && Marshal.ReadInt32(buffer, NativeMethods.WtsInfoExSessionFlagsOffset) == NativeMethods.WtsSessionStateLock)
                {
                    return true;
                }
            }
            finally
            {
                NativeMethods.WTSFreeMemory(buffer);
            }
        }

        var desktop = NativeMethods.OpenInputDesktop(0, false, NativeMethods.DesktopSwitchDesktop);
        if (desktop == IntPtr.Zero)
        {
            return true;
        }

        NativeMethods.CloseDesktop(desktop);
        return false;
    }

    private static bool IsScreenSaverRunning()
    {
        var running = 0;

        return NativeMethods.SystemParametersInfo(NativeMethods.SpiGetScreenSaverRunning, 0, ref running, 0)
            && running != 0;
    }

    /// <summary>
    /// Low-level keyboard and mouse hooks on a dedicated thread with its own message loop - the
    /// hooks are called on the thread that installed them, and only while it pumps messages. The
    /// callback does nothing but set a flag and pass the event on.
    /// </summary>
    /// <remarks>
    /// Fails closed: <see cref="Start"/> returns only once both hooks are installed and the thread's
    /// message queue exists, and throws otherwise. The hooks are removed only by the thread that
    /// installed them, in its <c>finally</c>; <see cref="Dispose"/> asks it to end and waits a
    /// bounded time. The start event is disposed only once the thread is known to be gone.
    /// </remarks>
    private sealed class LowLevelInputWatch : IRealInputWatch
    {
        private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

        private readonly ManualResetEventSlim _started = new();
        private readonly Thread _thread;

        // Kept in fields: the native side holds only a pointer, so a delegate collected by the GC
        // would crash the process on the next input event.
        private readonly LowLevelHookProc _keyboardProc;
        private readonly LowLevelHookProc _mouseProc;

        private uint _threadId;
        private int _startError;
        private int _stopRequested;
        private volatile bool _hooked;
        private volatile bool _detected;
        private int _disposed;

        private LowLevelInputWatch()
        {
            _keyboardProc = OnKeyboard;
            _mouseProc = OnMouse;
            _thread = new Thread(Run) { IsBackground = true, Name = "Desktop-Job input watch" };
        }

        public bool RealInputDetected => _detected;

        /// <summary>
        /// True when <see cref="Dispose"/> could not confirm the watch thread ended; its hooks may
        /// still be installed until it does.
        /// </summary>
        public bool StopUnconfirmed { get; private set; }

        /// <summary>Starts the watch thread and waits until both hooks are installed.</summary>
        /// <returns>The running watch.</returns>
        /// <exception cref="InvalidOperationException">The hooks could not be installed in time; nothing is left running.</exception>
        public static LowLevelInputWatch Start()
        {
            var watch = new LowLevelInputWatch();
            watch._thread.Start();

            var started = watch._started.Wait(StartTimeout);
            if (started && watch._hooked)
            {
                return watch;
            }

            var error = Volatile.Read(ref watch._startError);
            watch.Dispose();

            throw new InvalidOperationException(started
                ? string.Create(CultureInfo.InvariantCulture, $"Die Eingabe-Hooks konnten nicht installiert werden (Win32 {error}).")
                : "Die Eingabe-Hooks wurden nicht rechtzeitig installiert.");
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            // The flag first: a thread that has not created its queue yet sees it once the queue
            // exists, and one that already has receives the WM_QUIT.
            Interlocked.Exchange(ref _stopRequested, 1);

            var threadId = Volatile.Read(ref _threadId);
            if (threadId != 0 && !NativeMethods.PostThreadMessage(threadId, NativeMethods.WmQuit, IntPtr.Zero, IntPtr.Zero))
            {
                Trace.TraceWarning(
                    "Desktop-Job input watch: PostThreadMessage(WM_QUIT) failed (Win32 {0}).",
                    Marshal.GetLastPInvokeError());
            }

            if (_thread.Join(StopTimeout))
            {
                _started.Dispose();
                return;
            }

            // The thread still owns its hooks and may still touch _started: neither is released
            // here. It unhooks in its own finally whenever its loop ends.
            StopUnconfirmed = true;
            Trace.TraceWarning("Desktop-Job input watch: the hook thread did not end within {0}.", StopTimeout);
        }

        private void Run()
        {
            Volatile.Write(ref _threadId, NativeMethods.GetCurrentThreadId());

            // Creates this thread's message queue, so a WM_QUIT posted from now on is not lost.
            NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, NativeMethods.PmNoRemove);

            var module = NativeMethods.GetModuleHandle(null);

            // Locals, not fields: the hooks never leave this thread, which installs them, pumps
            // for them and removes them when its loop ends.
            var keyboardHook = IntPtr.Zero;
            var mouseHook = IntPtr.Zero;

            try
            {
                keyboardHook = NativeMethods.SetWindowsHookEx(NativeMethods.WhKeyboardLl, _keyboardProc, module, 0);
                if (keyboardHook == IntPtr.Zero)
                {
                    Volatile.Write(ref _startError, Marshal.GetLastPInvokeError());
                }

                mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WhMouseLl, _mouseProc, module, 0);
                if (mouseHook == IntPtr.Zero)
                {
                    Volatile.Write(ref _startError, Marshal.GetLastPInvokeError());
                }

                _hooked = keyboardHook != IntPtr.Zero && mouseHook != IntPtr.Zero;
                _started.Set();

                if (!_hooked || Volatile.Read(ref _stopRequested) != 0)
                {
                    return;
                }

                // 0 is WM_QUIT, -1 an error; either ends the loop.
                while (NativeMethods.GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
                {
                }
            }
            finally
            {
                if (keyboardHook != IntPtr.Zero)
                {
                    NativeMethods.UnhookWindowsHookEx(keyboardHook);
                }

                if (mouseHook != IntPtr.Zero)
                {
                    NativeMethods.UnhookWindowsHookEx(mouseHook);
                }
            }
        }

        // KBDLLHOOKSTRUCT: vkCode, scanCode, flags - flags at offset 8. CallNextHookEx ignores its
        // hook handle argument since Windows NT, so none is kept for it.
        private IntPtr OnKeyboard(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && !LowLevelInputFlags.IsInjectedKeyboard(unchecked((uint)Marshal.ReadInt32(lParam, 8))))
            {
                _detected = true;
            }

            return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        // MSLLHOOKSTRUCT: POINT pt (8 bytes), mouseData, flags - flags at offset 12.
        private IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && !LowLevelInputFlags.IsInjectedMouse(unchecked((uint)Marshal.ReadInt32(lParam, 12))))
            {
                _detected = true;
            }

            return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }
    }
}
