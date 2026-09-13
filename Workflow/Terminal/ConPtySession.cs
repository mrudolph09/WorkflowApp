using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Workflow.Terminal.Native;

namespace Workflow.Terminal;

/// <inheritdoc cref="ITerminalSession" />
public sealed class ConPtySession : ITerminalSession
{
    private const int ReadBufferSize = 4096;

    private readonly CancellationTokenSource _lifetime = new();

    private SafePseudoConsoleHandle? _pseudoConsole;
    private SafeProcThreadAttributeList? _attributes;
    private SafeFileHandle? _appWrite;
    private SafeFileHandle? _appRead;
    private FileStream? _input;
    private FileStream? _output;
    private Task? _readLoop;
    private int _processId;
    private IntPtr _processHandle = IntPtr.Zero;
    private int _disposed;

    /// <inheritdoc />
    public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;

    /// <inheritdoc />
    public event EventHandler<int>? Exited;

    /// <inheritdoc />
    public bool IsRunning { get; private set; }

    /// <inheritdoc />
    public void Start(string executable, string arguments, string workingDirectory, int columns, int rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);

        if (IsRunning)
        {
            throw new InvalidOperationException("Diese Terminal-Sitzung läuft bereits.");
        }

        // Two pipes: the PTY ends are handed to CreatePseudoConsole, the app ends stay here.
        if (!NativeMethods.CreatePipe(out var ptyInRead, out var appWrite, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (input) ist fehlgeschlagen.");
        }

        if (!NativeMethods.CreatePipe(out var appRead, out var ptyOutWrite, IntPtr.Zero, 0))
        {
            ptyInRead.Dispose();
            appWrite.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (output) ist fehlgeschlagen.");
        }

        _appWrite = appWrite;
        _appRead = appRead;

        var size = new Coord { X = (short)columns, Y = (short)rows };
        var hr = NativeMethods.CreatePseudoConsole(size, ptyInRead, ptyOutWrite, 0, out var hpc);

        // The pseudo-console duplicated these; keeping our copies open would prevent EOF.
        ptyInRead.Dispose();
        ptyOutWrite.Dispose();

        if (hr == NativeMethods.ENotImpl)
        {
            throw new PlatformNotSupportedException(
                "Diese Windows-Version unterstützt keine Pseudo-Konsole. Workflow benötigt Windows 10 Version 1809 oder neuer.");
        }

        Marshal.ThrowExceptionForHR(hr);

        _pseudoConsole = new SafePseudoConsoleHandle(hpc);
        _attributes = SafeProcThreadAttributeList.Create(_pseudoConsole);

        var startupInfo = default(StartupInfoEx);
        startupInfo.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
        startupInfo.lpAttributeList = _attributes.DangerousGetHandle();

        var commandLine = $"\"{executable}\" {arguments}";

        if (!NativeMethods.CreateProcess(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                bInheritHandles: false,
                NativeMethods.ExtendedStartupInfoPresent | NativeMethods.CreateUnicodeEnvironment,
                IntPtr.Zero,
                string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory,
                ref startupInfo,
                out var processInformation))
        {
            var error = Marshal.GetLastWin32Error();
            Cleanup();
            throw new Win32Exception(error, $"Der Shell-Prozess konnte nicht gestartet werden: {commandLine}");
        }

        NativeMethods.CloseHandle(processInformation.hThread);
        _processHandle = processInformation.hProcess;
        _processId = processInformation.dwProcessId;

        _input = new FileStream(_appWrite, FileAccess.Write, bufferSize: 1, isAsync: false);
        _output = new FileStream(_appRead, FileAccess.Read, bufferSize: ReadBufferSize, isAsync: false);

        IsRunning = true;
        _readLoop = Task.Run(() => ReadLoop(_lifetime.Token), CancellationToken.None);
        _ = Task.Run(WaitForExit, CancellationToken.None);
    }

    /// <inheritdoc />
    public void Write(ReadOnlySpan<byte> data)
    {
        var stream = _input ?? throw new InvalidOperationException("Die Terminal-Sitzung wurde noch nicht gestartet.");

        if (data.IsEmpty)
        {
            return;
        }

        try
        {
            stream.Write(data);
            stream.Flush();
        }
        catch (IOException)
        {
            // The shell has gone away; the Exited event reports it.
        }
        catch (ObjectDisposedException)
        {
            // Disposed concurrently.
        }
    }

    /// <inheritdoc />
    public void Resize(int columns, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);

        if (_pseudoConsole is null || _pseudoConsole.IsInvalid || _pseudoConsole.IsClosed)
        {
            return;
        }

        var size = new Coord { X = (short)columns, Y = (short)rows };
        // CA1806: the HRESULT must be consumed. A resize failure is not fatal here - the terminal
        // simply keeps its previous size - so it is intentionally discarded rather than throwing.
        _ = NativeMethods.ResizePseudoConsole(_pseudoConsole.DangerousGetHandle(), size);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        IsRunning = false;
        _lifetime.Cancel();

        // Order matters. ClosePseudoConsole can block until the attached client exits, so the
        // process tree is killed first; the pipes are closed last so the read loop sees EOF.
        KillProcessTree();
        _pseudoConsole?.Dispose();
        _attributes?.Dispose();

        try
        {
            _input?.Dispose();
        }
        catch (IOException)
        {
            // Already broken.
        }

        try
        {
            _output?.Dispose();
        }
        catch (IOException)
        {
            // Already broken.
        }

        try
        {
            _readLoop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The loop faulted during teardown; nothing to do.
        }

        if (_processHandle != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_processHandle);
            _processHandle = IntPtr.Zero;
        }

        _lifetime.Dispose();
    }

    private void Cleanup()
    {
        _attributes?.Dispose();
        _pseudoConsole?.Dispose();
        _appWrite?.Dispose();
        _appRead?.Dispose();
        _attributes = null;
        _pseudoConsole = null;
        _appWrite = null;
        _appRead = null;
    }

    private void KillProcessTree()
    {
        if (_processId == 0)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(_processId);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(3000);
        }
        catch (ArgumentException)
        {
            // Already exited.
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (Win32Exception)
        {
            // Access denied during shutdown; nothing further can be done.
        }
    }

    private void WaitForExit()
    {
        try
        {
            using var process = Process.GetProcessById(_processId);
            process.WaitForExit();
            IsRunning = false;
            Exited?.Invoke(this, process.ExitCode);
        }
        catch (ArgumentException)
        {
            IsRunning = false;
        }
        catch (InvalidOperationException)
        {
            IsRunning = false;
        }
    }

    private void ReadLoop(CancellationToken cancellationToken)
    {
        var stream = _output;
        if (stream is null)
        {
            return;
        }

        var buffer = new byte[ReadBufferSize];

        while (!cancellationToken.IsCancellationRequested)
        {
            int count;
            try
            {
                count = stream.Read(buffer, 0, buffer.Length);
            }
#pragma warning disable CA1031 // The read loop is a resilience boundary: no failure here may crash the app.
            catch (Exception)
#pragma warning restore CA1031
            {
                return;
            }

            if (count <= 0)
            {
                return;
            }

            // Raw bytes only. Decoding here would split multi-byte UTF-8 across reads.
            var chunk = new byte[count];
            Array.Copy(buffer, chunk, count);
            OutputReceived?.Invoke(this, chunk);
        }
    }
}
