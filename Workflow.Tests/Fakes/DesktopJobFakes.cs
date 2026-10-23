using System.ComponentModel;
using System.IO;
using Workflow.Models;
using Workflow.Services;

namespace Workflow.Tests.Fakes;

/// <summary>A clock whose delays complete at once and advance the time by exactly what was asked.</summary>
internal sealed class FakeDesktopJobClock : IDesktopJobClock
{
    private readonly object _gate = new();
    private DateTimeOffset _now = new(2026, 10, 11, 22, 0, 0, TimeSpan.Zero);

    /// <summary>Runs after every delay, with the delay's ordinal - lets a test act "between two polls".</summary>
    public Action<int>? OnDelay { get; set; }

    public List<TimeSpan> Delays { get; } = [];

    public DateTimeOffset UtcNow
    {
        get
        {
            lock (_gate)
            {
                return _now;
            }
        }
    }

    public async Task DelayAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        int ordinal;
        lock (_gate)
        {
            _now += interval;
            Delays.Add(interval);
            ordinal = Delays.Count;
        }

        OnDelay?.Invoke(ordinal);

        // Yields so a loop polling this clock never runs as one synchronous stack.
        await Task.Yield();
    }
}

/// <summary>An activity monitor whose answers the test sets.</summary>
internal sealed class FakeUserActivityMonitor : IUserActivityMonitor
{
    public UserActivity Activity { get; set; } = new(TimeSpan.FromHours(1), false, false);

    public int Reads { get; private set; }

    public FakeRealInputWatch? LastWatch { get; private set; }

    public int WatchesStarted { get; private set; }

    /// <summary>When true, StartRealInputWatch fails as a hook that could not be installed does.</summary>
    public bool WatchUnavailable { get; set; }

    /// <summary>Runs inside every Read, before the activity is returned.</summary>
    public Action? OnRead { get; set; }

    public UserActivity Read()
    {
        Reads++;
        OnRead?.Invoke();
        return Activity;
    }

    public IRealInputWatch StartRealInputWatch()
    {
        if (WatchUnavailable)
        {
            throw new InvalidOperationException("Hook nicht installierbar");
        }

        WatchesStarted++;
        LastWatch = new FakeRealInputWatch();
        return LastWatch;
    }
}

/// <summary>
/// Classifies reported low-level events exactly as the Win32 hook does, through the same pure filter,
/// so a test can show that injected input (FlaUI's) is not taken for the user's.
/// </summary>
internal sealed class FakeRealInputWatch : IRealInputWatch
{
    private volatile bool _detected;

    public bool RealInputDetected => _detected;

    public bool Disposed { get; private set; }

    public void ReportKeyboard(uint flags)
    {
        if (!LowLevelInputFlags.IsInjectedKeyboard(flags))
        {
            _detected = true;
        }
    }

    public void ReportMouse(uint flags)
    {
        if (!LowLevelInputFlags.IsInjectedMouse(flags))
        {
            _detected = true;
        }
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeCpuLoadProbe : ICpuLoadProbe
{
    public double Load { get; set; } = 5;

    public List<TimeSpan> Windows { get; } = [];

    /// <summary>When true, the measurement fails as GetSystemTimes failing does.</summary>
    public bool Unavailable { get; set; }

    public Task<double> MeasureAsync(TimeSpan window, CancellationToken cancellationToken)
    {
        Windows.Add(window);

        return Unavailable
            ? Task.FromException<double>(new InvalidOperationException("GetSystemTimes fehlgeschlagen"))
            : Task.FromResult(Load);
    }
}

internal sealed class FakeForegroundYield : IForegroundYield
{
    public List<string> Calls { get; } = [];

    /// <summary>Runs inside every MinimiseAsync, after it is recorded.</summary>
    public Action? OnMinimise { get; set; }

    public Task MinimiseAsync()
    {
        Calls.Add("minimise");
        OnMinimise?.Invoke();
        return Task.CompletedTask;
    }

    public Task RestoreAsync()
    {
        Calls.Add("restore");
        return Task.CompletedTask;
    }
}

/// <summary>A process starter whose processes behave as the test configures them.</summary>
internal sealed class FakeDesktopJobProcessStarter : IDesktopJobProcessStarter
{
    /// <summary>Exit code of each started process; null keeps it running until killed.</summary>
    public int? ExitCode { get; set; } = 0;

    /// <summary>Whether a process that exits creates its step's marker first.</summary>
    public bool CreateMarker { get; set; } = true;

    /// <summary>Thrown by <see cref="Start"/> instead of starting anything.</summary>
    public Exception? StartFailure { get; set; }

    /// <summary>Runs right after a process starts, before the runner watches it.</summary>
    public Action<FakeDesktopJobProcess>? OnStart { get; set; }

    /// <summary>Maps the launch to the marker path the process should create.</summary>
    public Func<DesktopJobLaunch, string?>? MarkerFor { get; set; }

    public List<DesktopJobLaunch> Launches { get; } = [];

    public List<FakeDesktopJobProcess> Processes { get; } = [];

    public IDesktopJobProcess Start(DesktopJobLaunch launch)
    {
        if (StartFailure is not null)
        {
            throw StartFailure;
        }

        Launches.Add(launch);
        var process = new FakeDesktopJobProcess();
        Processes.Add(process);
        OnStart?.Invoke(process);

        if (ExitCode is { } code && !process.Killed)
        {
            if (CreateMarker && MarkerFor?.Invoke(launch) is { } marker)
            {
                File.WriteAllText(marker, "ok");
            }

            process.Exit(code);
        }

        return process;
    }

    public static Win32Exception NotFound() => new(2, "pwsh nicht gefunden");
}

internal sealed class FakeDesktopJobProcess : IDesktopJobProcess
{
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Killed { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>Number of TerminateAsync calls so far.</summary>
    public int Terminations { get; private set; }

    /// <summary>What TerminateAsync reports: true for a process tree confirmed gone.</summary>
    public bool TerminateConfirms { get; set; } = true;

    /// <summary>Runs synchronously inside TerminateAsync - the moment the teardown is requested.</summary>
    public Action? OnTerminate { get; set; }

    /// <summary>Runs synchronously inside Dispose, before it is recorded.</summary>
    public Action? OnDispose { get; set; }

    public Task<int> Exited => _exit.Task;

    public void Exit(int code) => _exit.TrySetResult(code);

    public void Kill()
    {
        Killed = true;
        _exit.TrySetResult(-1);
    }

    public Task<bool> TerminateAsync(TimeSpan timeout)
    {
        OnTerminate?.Invoke();
        Terminations++;
        _exit.TrySetResult(-1);

        return Task.FromResult(TerminateConfirms);
    }

    public void Dispose()
    {
        OnDispose?.Invoke();
        Disposed = true;
    }
}
