using System.ComponentModel;
using System.Globalization;
using System.IO;
using Workflow.Models;

namespace Workflow.Services;

/// <summary>What one call of <see cref="DesktopJobRunner.RunNextStepAsync"/> did.</summary>
/// <param name="RequestId">The request the step belongs to, or null when none could be read.</param>
/// <param name="StepId">The step that was considered, or null when there was none.</param>
/// <param name="Outcome">
/// One of <see cref="DesktopJobOutcome"/>; null when nothing started because the idle window was
/// closed (no result is written then). <see cref="DesktopJobOutcome.Completed"/> with a null
/// <paramref name="StepId"/> means every step had already completed.
/// </param>
/// <param name="JobComplete">True when, after this call, every step has completed for the current request.</param>
/// <param name="Reason">
/// German explanation of a <see cref="DesktopJobOutcome.StartFailed"/>, or of an
/// <see cref="DesktopJobOutcome.AbortedBusy"/> whose load could not be measured; otherwise null.
/// </param>
public sealed record DesktopJobStepReport(
    string? RequestId,
    string? StepId,
    string? Outcome,
    bool JobComplete,
    string? Reason = null)
{
    /// <summary>Whether this outcome uses up one of the step's attempts in the current run.</summary>
    public bool CountsAsAttempt => DesktopJobOutcome.CountsAsAttempt(Outcome);
}

/// <summary>
/// Runs the next step of one subtask's desktop job: re-checks the idle window, requires a quiet
/// machine, watches for real user input, gets the application's window out of the way, starts the
/// script and watches it for real user input, a lock and its timeout, tears its whole process tree
/// down, and only then records the outcome - authoritatively in application-owned storage
/// (<see cref="DesktopJobFiles.AuthoritativeResultPath"/>) and as an informational
/// <c>desktopjob.result.json</c> in the subtask folder. A start the user asked for skips the idle
/// time and the CPU check; the lock and screen-saver check and the real-input watch stay in force.
/// </summary>
/// <remarks>
/// <para>
/// The caller holds the <see cref="IExecutionGate"/> exclusively around a call, so no agent session
/// runs while a step does; the runner itself knows nothing of the gate or of the subtask loop.
/// </para>
/// <para>
/// Steps run strictly in order, starting at the first one without a completed result for the
/// current request, config and script (<see cref="DesktopJobFiles.NextStepIndex"/>). A result for
/// another request id, or for an edited request, is ignored entirely and the job starts fresh.
/// </para>
/// </remarks>
public sealed class DesktopJobRunner
{
    /// <summary>Average system CPU load, in percent, at or above which a step does not start.</summary>
    public const double BusyThresholdPercent = 25;

    /// <summary>How long the CPU load is averaged before a step starts.</summary>
    public static readonly TimeSpan CpuWindow = TimeSpan.FromSeconds(60);

    /// <summary>The reason recorded with an <see cref="DesktopJobOutcome.AbortedBusy"/> whose load could not be measured.</summary>
    public const string CpuLoadUnknownReason = "CPU-Last unbekannt";

    /// <summary>The reason recorded with a <see cref="DesktopJobOutcome.StartFailed"/> whose input watch could not be installed.</summary>
    public const string InputWatchUnavailableReason = "Eingabeüberwachung nicht verfügbar";

    private static readonly TimeSpan DefaultMonitorInterval = TimeSpan.FromMilliseconds(500);

    // How long a finished, aborted or timed-out step's process tree may take to be confirmed gone.
    private static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(10);

    private readonly IUserActivityMonitor _monitor;
    private readonly ICpuLoadProbe _cpu;
    private readonly IDesktopJobProcessStarter _starter;
    private readonly IForegroundYield? _foregroundYield;
    private readonly TimeSpan _monitorInterval;

    /// <summary>Creates the runner.</summary>
    /// <param name="monitor">Reports idle time, lock and screen saver, and watches for real input.</param>
    /// <param name="cpu">Measures the system CPU load.</param>
    /// <param name="starter">Starts the step scripts.</param>
    /// <param name="clock">The time source and waiting; defaults to the system clock.</param>
    /// <param name="foregroundYield">Minimises and restores the main window; null does neither.</param>
    /// <param name="monitorInterval">How often a running step is checked; defaults to 500 ms.</param>
    public DesktopJobRunner(
        IUserActivityMonitor monitor,
        ICpuLoadProbe cpu,
        IDesktopJobProcessStarter starter,
        IDesktopJobClock? clock = null,
        IForegroundYield? foregroundYield = null,
        TimeSpan? monitorInterval = null)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(cpu);
        ArgumentNullException.ThrowIfNull(starter);

        _monitor = monitor;
        _cpu = cpu;
        _starter = starter;
        Clock = clock ?? new SystemDesktopJobClock();
        _foregroundYield = foregroundYield;
        _monitorInterval = monitorInterval ?? DefaultMonitorInterval;
    }

    /// <summary>The clock the runner measures with; the orchestrator polls the idle window with it too.</summary>
    public IDesktopJobClock Clock { get; }

    /// <summary>Reads the user's current activity.</summary>
    /// <returns>Idle time, lock and screen saver state.</returns>
    public UserActivity ReadActivity() => _monitor.Read();

    /// <summary>Runs the next step of the desktop job in <paramref name="subtaskDirectory"/>.</summary>
    /// <param name="subtaskDirectory">The subtask folder holding <c>desktopjob.json</c> and the scripts.</param>
    /// <param name="workingDirectory">The product directory the script runs in.</param>
    /// <param name="skipIdleWait">
    /// True for a start the user asked for: it starts right away - no idle minutes, no CPU
    /// measurement - but the desktop must still be unlocked without a screen saver, and real input
    /// once the step runs still aborts it.
    /// </param>
    /// <param name="cancellationToken">Kills a running step and ends the call with an <see cref="OperationCanceledException"/>.</param>
    /// <returns>What happened.</returns>
    public async Task<DesktopJobStepReport> RunNextStepAsync(
        string subtaskDirectory,
        string workingDirectory,
        bool skipIdleWait,
        CancellationToken cancellationToken)
    {
        var read = DesktopJobFiles.ReadRequest(subtaskDirectory);
        if (read.Request is not { } request)
        {
            // Needs no idle window: nothing is started, and the verification session should see why.
            return RecordInvalidRequest(subtaskDirectory, read);
        }

        var job = Prepare(subtaskDirectory, request);
        if (job.NextIndex < 0)
        {
            return new DesktopJobStepReport(read.RequestId, null, DesktopJobOutcome.Completed, JobComplete: true);
        }

        var step = request.Steps[job.NextIndex];

        // Hashed once: the guard below and the launch use this same value.
        var scriptSha = DesktopJobFiles.ScriptSha256(subtaskDirectory, step.Script);
        if (DesktopJobFiles.RepairOutcome(job.Recorded, step, scriptSha) is { } failed)
        {
            // G1: this step already ran and failed with this very script for this request. Nothing is
            // launched or recorded (and no CPU check may overwrite the entry with aborted_busy); the
            // orchestrator hands the entry to a repair session instead.
            return new DesktopJobStepReport(read.RequestId, step.Id, failed, JobComplete: false);
        }

        // IsOpen(activity, 0) still requires an unlocked desktop without a screen saver.
        var idleMinutes = skipIdleWait ? 0 : request.IdleMinutes;
        if (!IdleWindowPolicy.IsOpen(_monitor.Read(), idleMinutes))
        {
            return new DesktopJobStepReport(read.RequestId, step.Id, null, JobComplete: false);
        }

        // A start the user asked for is not measured: the minute of quiet it would need is exactly
        // the minute the user, who just clicked, cannot be expected to keep still.
        if (!skipIdleWait)
        {
            double load;
            try
            {
                load = await _cpu.MeasureAsync(CpuWindow, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // An unknown load is not a quiet machine: treated like a busy one, retried later.
                var now = Clock.UtcNow;
                return Record(job, step, new StepAttempt(DesktopJobOutcome.AbortedBusy, null, now, now, null), CpuLoadUnknownReason);
            }

            if (load >= BusyThresholdPercent)
            {
                var now = Clock.UtcNow;
                return Record(job, step, new StepAttempt(DesktopJobOutcome.AbortedBusy, null, now, now, null));
            }
        }

        // A start the user asked for re-checks only the lock and the screen saver.
        var recheckIdleMinutes = skipIdleWait ? 0 : request.IdleMinutes;

        return await RunStepAsync(job, step, scriptSha, workingDirectory, recheckIdleMinutes, cancellationToken);
    }

    /// <summary>
    /// Starts the input watch, re-checks the idle window, yields the foreground and - only if no
    /// real input arrived meanwhile - starts the step and watches it.
    /// </summary>
    /// <remarks>
    /// The watch comes first so that no input between the idle re-check and the start of pwsh can
    /// go unnoticed. The result is recorded only once the step's whole process tree is gone and
    /// disposed, so nothing that reads it can meet a process of the step still running.
    /// </remarks>
    private async Task<DesktopJobStepReport> RunStepAsync(
        PreparedJob job,
        DesktopJobStep step,
        string? scriptSha,
        string workingDirectory,
        int recheckIdleMinutes,
        CancellationToken cancellationToken)
    {
        if (scriptSha is null)
        {
            var now = Clock.UtcNow;
            return Record(
                job,
                step,
                new StepAttempt(DesktopJobOutcome.StartFailed, null, now, now, null),
                $"Skript '{step.Script}' fehlt oder ist nicht lesbar.");
        }

        IRealInputWatch watch;
        try
        {
            watch = _monitor.StartRealInputWatch();
        }
        catch (InvalidOperationException)
        {
            // Fail closed: without the watch the user's return would go unnoticed.
            var now = Clock.UtcNow;
            return Record(job, step, new StepAttempt(DesktopJobOutcome.StartFailed, null, now, now, scriptSha), InputWatchUnavailableReason);
        }

        using (watch)
        {
            // The CPU measurement took a minute; the user may be back (or, on a start the user
            // asked for, may have locked the desktop meanwhile).
            if (!IdleWindowPolicy.IsOpen(_monitor.Read(), recheckIdleMinutes))
            {
                return new DesktopJobStepReport(job.Request.RequestId.ToString("D"), step.Id, null, JobComplete: false);
            }

            var logPath = Path.Combine(job.Directory, DesktopJobFiles.LogFileName(step.Id));
            AppendLogHeader(logPath, step, job.PreviousAttempts(step.Id) + 1, Clock.UtcNow);

            if (!TryDeleteStaleMarker(step, logPath))
            {
                var now = Clock.UtcNow;
                return Record(
                    job,
                    step,
                    new StepAttempt(DesktopJobOutcome.StartFailed, null, now, now, scriptSha),
                    $"Alter Marker '{step.Marker}' konnte nicht gelöscht werden.");
            }

            if (_foregroundYield is not null)
            {
                await _foregroundYield.MinimiseAsync();
            }

            try
            {
                // The last moment before pwsh: input that arrived while the window was yielded.
                if (watch.RealInputDetected)
                {
                    var now = Clock.UtcNow;
                    return Record(job, step, new StepAttempt(DesktopJobOutcome.AbortedUserInput, null, now, now, scriptSha));
                }

                var started = Clock.UtcNow;

                IDesktopJobProcess process;
                try
                {
                    process = _starter.Start(new DesktopJobLaunch(
                        Path.Combine(job.Directory, step.Script), workingDirectory, logPath));
                }
                catch (DesktopJobStartException ex)
                {
                    return Record(
                        job,
                        step,
                        new StepAttempt(DesktopJobOutcome.StartFailed, null, started, Clock.UtcNow, scriptSha),
                        ex.Message);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    return Record(
                        job,
                        step,
                        new StepAttempt(DesktopJobOutcome.StartFailed, null, started, Clock.UtcNow, scriptSha),
                        $"pwsh konnte nicht gestartet werden: {ex.Message}");
                }

                StepAttempt attempt;
                using (process)
                {
                    attempt = await WatchAsync(process, watch, step, started, scriptSha, cancellationToken);

                    // Also after a normal exit: a script may leave an application under test behind.
                    if (!await process.TerminateAsync(TeardownTimeout))
                    {
                        AppendLogLine(logPath, "Prozessbaum ließ sich nicht bestätigt beenden; das Job-Objekt wird geschlossen.");
                    }
                }

                return Record(job, step, attempt);
            }
            finally
            {
                if (_foregroundYield is not null)
                {
                    await _foregroundYield.RestoreAsync();
                }
            }
        }
    }

    /// <summary>
    /// Deletes a marker an earlier attempt or anyone else left behind, so only this attempt can
    /// produce it; logs the deletion.
    /// </summary>
    /// <returns>False when an existing marker could not be deleted.</returns>
    private static bool TryDeleteStaleMarker(DesktopJobStep step, string logPath)
    {
        try
        {
            if (!File.Exists(step.Marker))
            {
                return true;
            }

            File.Delete(step.Marker);
            AppendLogLine(logPath, $"Alter Marker gelöscht: {step.Marker}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppendLogLine(logPath, $"Alter Marker konnte nicht gelöscht werden: {step.Marker} ({ex.Message})");
            return false;
        }
    }

    /// <summary>Waits for the step to exit while checking for real input, a lock and the timeout.</summary>
    private async Task<StepAttempt> WatchAsync(
        IDesktopJobProcess process,
        IRealInputWatch watch,
        DesktopJobStep step,
        DateTimeOffset started,
        string scriptSha,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMinutes(step.TimeoutMinutes);

        while (true)
        {
            if (process.Exited.IsCompleted)
            {
                var ended = Clock.UtcNow;
                var exitCode = await process.Exited;
                var markerFound = File.Exists(step.Marker);
                var outcome = exitCode == 0 && markerFound ? DesktopJobOutcome.Completed : DesktopJobOutcome.ScriptFailed;

                return new StepAttempt(outcome, exitCode, started, ended, scriptSha, markerFound);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                process.Kill();
                cancellationToken.ThrowIfCancellationRequested();
            }

            var aborted = watch.RealInputDetected ? DesktopJobOutcome.AbortedUserInput
                : _monitor.Read().IsLocked ? DesktopJobOutcome.AbortedLocked
                : Clock.UtcNow - started >= timeout ? DesktopJobOutcome.Timeout
                : null;

            if (aborted is not null)
            {
                // Taken before the kill, so a timeout ends at exactly its limit.
                var ended = Clock.UtcNow;
                process.Kill();

                return new StepAttempt(aborted, null, started, ended, scriptSha, File.Exists(step.Marker));
            }

            // A cancelled delay completes WhenAny without throwing; the check above then kills.
            await Task.WhenAny(process.Exited, Clock.DelayAsync(_monitorInterval, cancellationToken));
        }
    }

    private static PreparedJob Prepare(string directory, DesktopJobRequest request)
    {
        var matching = DesktopJobFiles.MatchingResult(request, DesktopJobFiles.ReadResult(directory));

        return new PreparedJob(
            directory,
            request,
            DesktopJobFiles.ConfigSha256(request),
            matching?.Steps ?? [],
            DesktopJobFiles.NextStepIndex(directory, request, matching));
    }

    /// <summary>Records one attempt of one step and reports it.</summary>
    /// <remarks>
    /// The other steps' entries for this request are kept, in step order; the result is rewritten
    /// whole, atomically. A result that cannot be written turns the outcome into a counted
    /// <see cref="DesktopJobOutcome.StartFailed"/>: an unrecorded completion would otherwise be run
    /// again and again without ever counting against the attempt limit.
    /// </remarks>
    private static DesktopJobStepReport Record(
        PreparedJob job,
        DesktopJobStep step,
        StepAttempt attempt,
        string? reason = null)
    {
        var entry = new DesktopJobStepResult(
            step.Id,
            attempt.ScriptSha,
            attempt.Outcome,
            attempt.ExitCode,
            attempt.MarkerFound,
            attempt.Started,
            attempt.Ended,
            job.PreviousAttempts(step.Id) + 1,
            reason);

        var steps = job.Request.Steps
            .Select(candidate => candidate.Id == step.Id
                ? entry
                : job.Recorded.FirstOrDefault(recorded => string.Equals(recorded.StepId, candidate.Id, StringComparison.Ordinal)))
            .OfType<DesktopJobStepResult>()
            .ToList();

        var result = new DesktopJobResult(job.Request.RequestId.ToString("D"), job.ConfigSha, steps);

        try
        {
            DesktopJobFiles.WriteResult(job.Directory, result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DesktopJobStepReport(
                result.RequestId,
                step.Id,
                DesktopJobOutcome.StartFailed,
                JobComplete: false,
                $"{DesktopJobFiles.ResultFileName} konnte nicht geschrieben werden: {ex.Message}");
        }

        var complete = DesktopJobFiles.NextStepIndex(job.Directory, job.Request, result) < 0;

        return new DesktopJobStepReport(result.RequestId, step.Id, attempt.Outcome, complete, reason);
    }

    private static DesktopJobStepReport RecordInvalidRequest(string directory, DesktopJobRequestRead read)
    {
        try
        {
            DesktopJobFiles.WriteResult(directory, new DesktopJobResult(read.RequestId, null, [], read.Error));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The reason still reaches the run's blocked message through the report.
        }

        return new DesktopJobStepReport(read.RequestId, null, DesktopJobOutcome.StartFailed, JobComplete: false, read.Error);
    }

    private static void AppendLogLine(string logPath, string line)
    {
        try
        {
            File.AppendAllText(logPath, line + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A missing log line costs the log its detail, not the step its run.
        }
    }

    private static void AppendLogHeader(string logPath, DesktopJobStep step, int attempt, DateTimeOffset now)
    {
        try
        {
            File.AppendAllText(
                logPath,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"=== Desktop-Job {step.Id} - Versuch {attempt} - {now:O} - {step.Script} ==={Environment.NewLine}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A missing header costs the log its separator, not the step its run.
        }
    }

    /// <summary>The request, its matching earlier result and the step to run next.</summary>
    private sealed record PreparedJob(
        string Directory,
        DesktopJobRequest Request,
        string ConfigSha,
        IReadOnlyList<DesktopJobStepResult> Recorded,
        int NextIndex)
    {
        public int PreviousAttempts(string stepId) =>
            Recorded.FirstOrDefault(entry => string.Equals(entry.StepId, stepId, StringComparison.Ordinal))?.Attempts ?? 0;
    }

    /// <summary>What one attempt produced, before it is recorded.</summary>
    private sealed record StepAttempt(
        string Outcome,
        int? ExitCode,
        DateTimeOffset Started,
        DateTimeOffset Ended,
        string? ScriptSha,
        bool MarkerFound = false);
}
