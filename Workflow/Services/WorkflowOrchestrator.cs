using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Workflow.Models;
using Workflow.Terminal;

namespace Workflow.Services;

/// <inheritdoc cref="IWorkflowOrchestrator" />
public sealed class WorkflowOrchestrator : IWorkflowOrchestrator
{
    private const int SnapshotLines = 60;

    /// <summary>Marker the shell prints immediately before the launcher is invoked.</summary>
    /// <remarks>
    /// Seeing it is the only proof that the shell actually executed the line we typed. Without it
    /// an unrecognised screen is indistinguishable from a shell that never ran anything, and the
    /// prompt would be pasted into the latter.
    /// </remarks>
    private const string RunMarkerPrefix = "WF-RUN-";

    /// <summary>Marker the shell prints once the launcher has returned control to it.</summary>
    /// <remarks>
    /// Seeing it is proof that the launcher is gone and the shell owns stdin again. The rendered
    /// prompt of a shell is deliberately NOT used for this: it varies with the user's profile
    /// (the session runs pwsh without -NoProfile), wraps in a 120-column buffer, differs for UNC
    /// paths, and has a continuation form - every one of which would read as "not a shell" and let
    /// a multi-kilobyte markdown prompt be executed line by line as commands.
    /// </remarks>
    private const string ReturnMarkerPrefix = "WF-RET-";

    /// <summary>How often the stalled wait re-reads the screen while the launcher is not running.</summary>
    private static readonly TimeSpan StallPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Shown while a session refuses to paste because the launcher is not running.</summary>
    private const string LauncherStalledMessage =
        "Der Launcher läuft nicht - das Terminal wartet. Starte ihn von Hand; " +
        "der Prompt wird dann automatisch gesendet.";

    /// <summary>Shown when a session's prompt submission could not be confirmed.</summary>
    /// <remarks>
    /// Neutral on purpose: the prompt may have been submitted while the expected footer failed to
    /// render, so an unconditional "press Enter" could itself send an unwanted input.
    /// </remarks>
    private const string PromptSubmissionUnconfirmedMessage =
        "Das automatische Absenden des Prompts konnte nicht bestätigt werden. Bitte im Terminal prüfen; " +
        "falls der Prompt noch im Eingabefeld steht, einmal Enter drücken.";

    /// <summary>How often the positive-evidence submit re-reads the screen.</summary>
    private static readonly TimeSpan EvidencePollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Prompt file that turns the existing spec and plan into the ordered subtask index.</summary>
    /// <remarks>
    /// Named here rather than taken from <see cref="PhaseCatalog"/>: decomposition is not a phase,
    /// and <see cref="PromptTemplateCatalog"/> keys the same literal for its token set.
    /// </remarks>
    private const string DecompositionPromptFile = "create_subtasks.md";

    /// <summary>Command typed into the shell to start the CLI for a decomposition session.</summary>
    /// <remarks>
    /// The design fixes <c>yo</c> for the decomposition and subtask sessions. It coincides with
    /// the implementation phase's launcher but is not derived from it: a phase definition describes
    /// a phase, and changing the implementation phase's launcher must not silently re-aim
    /// decomposition.
    /// </remarks>
    private const string DecompositionLauncher = "yo";

    /// <summary>How the decomposition session's prompt submission is verified.</summary>
    /// <remarks>Fixed alongside <see cref="DecompositionLauncher"/>: <c>yo</c> is Claude Code.</remarks>
    private const PromptSubmissionMode DecompositionSubmissionMode = PromptSubmissionMode.PositiveEvidence;

    /// <summary>Prompt file that executes exactly one entry of the ordered subtask index.</summary>
    /// <remarks>
    /// Keyed by the same literal in <see cref="PromptTemplateCatalog"/>, which maps it to
    /// <see cref="PromptVariables.SubtaskRunNames"/>.
    /// </remarks>
    private const string SubtaskRunPromptFile = "run_subtask.md";

    /// <summary>Command typed into the shell to start the CLI for one subtask session.</summary>
    /// <remarks>
    /// Deliberately its own constant rather than an alias of <see cref="DecompositionLauncher"/>,
    /// for the same reason that one is not an alias of the implementation phase's launcher:
    /// decomposition and execution are separate sessions, and re-aiming one must never silently
    /// re-aim the other.
    /// </remarks>
    private const string SubtaskRunLauncher = "yo";

    /// <summary>How a subtask session's prompt submission is verified.</summary>
    /// <remarks>Fixed alongside <see cref="SubtaskRunLauncher"/>: <c>yo</c> is Claude Code.</remarks>
    private const PromptSubmissionMode SubtaskRunSubmissionMode = PromptSubmissionMode.PositiveEvidence;

    /// <summary>Prompt file of the decision pre-flight, which writes the task's <c>decisions.md</c> (B7).</summary>
    /// <remarks>Keyed by the same literal in <see cref="PromptTemplateCatalog"/>.</remarks>
    private const string DecisionsPromptFile = "create_decisions.md";

    /// <summary>Command typed into the shell to start the CLI for the decision pre-flight.</summary>
    /// <remarks>Its own constant for the reason <see cref="SubtaskRunLauncher"/> gives.</remarks>
    private const string DecisionsLauncher = "yo";

    /// <summary>How the pre-flight session's prompt submission is verified.</summary>
    private const PromptSubmissionMode DecisionsSubmissionMode = PromptSubmissionMode.PositiveEvidence;

    /// <summary>
    /// System prompt appended to every unattended session - decomposition, pre-flight, subtask - that
    /// tells the agent there is nobody to ask (B6). Read by the CLI itself, never rendered.
    /// </summary>
    private const string NoHumanPromptFile = "no_human.md";

    /// <summary>Pasted into a session after its first stall, behind an Escape that interrupts whatever waits.</summary>
    private const string StallNudgePrompt =
        "Es gibt keinen Menschen. Beende die Subtask jetzt: entscheide nach decisions.md oder schreibe " +
        "status failed mit Grund, dann die Flag-Datei.";

    /// <summary>Shown while a run holds for the user's approval of the task's decisions.</summary>
    private const string DecisionsAwaitApprovalMessage = "Entscheidungen prüfen: decisions.md freigeben mit Play";

    /// <summary>Leads the hold's status line when Play met decisions that changed after they were shown.</summary>
    private const string DecisionsChangedMessage = "decisions.md wurde geändert – erneut prüfen";

    /// <summary>Pause between two attempts to read an attempted subtask's status payload.</summary>
    /// <remarks>
    /// Requirement 2.11 and design decision (issue 10): the settling interval is 200 ms. It is a
    /// constant rather than a parameter because the figure is part of the contract, not a tuning
    /// knob; only the <em>waiting</em> is injectable, and tests assert that this exact value is the
    /// one handed to the waiter.
    /// </remarks>
    private static readonly TimeSpan StatusSettleInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// How often an unusable status payload is re-read before the entry is left as the ledger finds
    /// it. Five retries follow the first read, so a payload is read at most six times and the whole
    /// settling is bounded by five intervals (requirement 2.11, E7).
    /// </summary>
    /// <remarks>
    /// This bound exists only for the payload. It is emphatically <em>not</em> a session timeout:
    /// waiting for the session itself is unbounded (requirement 2.8, E8), and this countdown starts
    /// only after that wait has already ended with a published flag.
    /// </remarks>
    private const int StatusSettleRetries = 5;

    /// <summary>
    /// How many subtasks in a row may end <see cref="SubtaskStatus.Failed"/> before a run stops
    /// starting new sessions. A heuristic, not a dependency claim: the index is only an ordered list,
    /// so one failure never stops a run, but two in a row usually mean a shared cause (for example
    /// foreign drift in the product repository) that every following session would trip over too.
    /// </summary>
    private const int MaxConsecutiveFailuresPerRun = 2;

    /// <summary>
    /// How many counted attempts (<see cref="DesktopJobStepReport.CountsAsAttempt"/>) one desktop
    /// job step gets per run before its job is exhausted and the run ends terminally blocked. The
    /// repair outcomes (<see cref="DesktopJobOutcome.NeedsRepair"/>) are not counted: they hand the
    /// entry to a repair session instead.
    /// </summary>
    private const int MaxCountedDesktopJobAttemptsPerRun = 2;

    /// <summary>
    /// How many verification or repair sessions - sessions for a deferred entry whose job delivered,
    /// or ran and failed - one entry gets per run. A session that defers again with a new request
    /// gets its job run and one more session; after that the entry waits for the next run.
    /// </summary>
    private const int MaxVerificationSessionsPerRun = 2;

    /// <summary>How often a run waiting for a desktop job's idle window checks it again.</summary>
    private static readonly TimeSpan DefaultDesktopJobPollInterval = TimeSpan.FromSeconds(30);

    /// <summary>How long a confirmed stop of a session's process tree may take.</summary>
    private static readonly TimeSpan SessionStopTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The run's error when a stalled session's process tree could not be confirmed gone.</summary>
    private const string SessionNotStoppedStatusNotWrittenMessage =
        "Session ließ sich nicht beenden; Status nicht geschrieben.";

    /// <summary>The run's error when the live session could not be confirmed gone before a desktop job.</summary>
    private const string SessionNotStoppedDesktopJobNotStartedMessage =
        "Session ließ sich nicht beenden; Desktop-Job nicht gestartet.";

    /// <summary>Shown while a session waits for a desktop job to release the execution gate.</summary>
    private const string SessionWaitsForDesktopJobMessage =
        "Ein Desktop-Job läuft - die Sitzung startet, sobald er beendet ist.";

    // Identical to the ledger's own reader options on purpose: "the parse succeeded" must mean the
    // same thing here as it does there, or settling would either give up on a payload the ledger
    // accepts or stop on one it rejects.
    private static readonly JsonDocumentOptions StatusDocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    // The status payload of a stalled subtask, written by the application in the session's own layout.
    private static readonly JsonSerializerOptions StalledOutcomeWriteOptions = new() { WriteIndented = true };

    private readonly IPromptTemplateService _prompts;
    /// <summary>Pause between the individual key presses of one auto-answer.</summary>
    private static readonly TimeSpan KeyPressGap = TimeSpan.FromMilliseconds(60);

    private readonly IAutoAnswerService _autoAnswer;
    private readonly IArtifactWatcherFactory _watchers;
    private readonly ITaskStateStore _state;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _pollInterval;
    private readonly Func<TimeSpan, CancellationToken, Task> _settleDelay;
    private readonly IExecutionGate _gate;
    private readonly DesktopJobRunner? _desktopJobs;
    private readonly TimeSpan _desktopJobPollInterval;
    private readonly StallPolicy _stall;
    private readonly IDesktopJobClock _clock;
    private readonly bool _requireDecisionApproval;
    private readonly string _noHumanPromptPath;

    /// <summary>Creates the orchestrator.</summary>
    /// <param name="prompts">Prompt renderer.</param>
    /// <param name="autoAnswer">Auto-answer rule engine.</param>
    /// <param name="watchers">Artefact watcher factory.</param>
    /// <param name="state">The per-task workflow journal.</param>
    /// <param name="debounce">Debounce applied to file-system notifications.</param>
    /// <param name="pollInterval">Fallback poll interval for dropped file-system events.</param>
    /// <param name="settleDelay">
    /// How the bounded status settling waits between two reads. Defaults to
    /// <see cref="Task.Delay(TimeSpan, CancellationToken)"/>. Only the <em>waiting</em> is
    /// substitutable; the interval itself stays <see cref="StatusSettleInterval"/> and is passed to
    /// this delegate, so a test can drive the retries deterministically and still observe that the
    /// contract's 200 ms is what was asked for.
    /// </param>
    /// <param name="gate">
    /// The application-wide execution gate: every unattended agent session (decomposition, decision
    /// pre-flight, subtask) holds it shared, every desktop job exclusively; phase sessions, which have
    /// a human in the loop, do not take it. Defaults to a private gate, which is enough while one
    /// orchestrator serves all tabs.
    /// </param>
    /// <param name="desktopJobs">
    /// Runs desktop jobs for deferred entries. Null - the default - leaves deferred entries waiting:
    /// a run that has nothing else to do ends with the desktop-job message instead of polling.
    /// </param>
    /// <param name="desktopJobPollInterval">
    /// How often a run waiting for a desktop job's idle window checks it again; defaults to 30 s.
    /// The waiting itself goes through <see cref="DesktopJobRunner.Clock"/>.
    /// </param>
    /// <param name="stall">
    /// When an unattended session counts as stalled; defaults to <see cref="StallPolicy.Default"/>.
    /// Phase sessions have a human at the keyboard and are never watched.
    /// </param>
    /// <param name="clock">
    /// The stall watchdog's time source and poll wait, and the approval's timestamp. Defaults to the
    /// desktop job runner's clock, or the system clock without one.
    /// </param>
    /// <param name="requireDecisionApproval">
    /// True - the default - runs the decision pre-flight (B7) and holds every subtask session until
    /// the task's <c>decisions.md</c> is approved.
    /// </param>
    /// <param name="noHumanPromptPath">
    /// The system prompt every unattended session appends; defaults to
    /// <c>AppContext.BaseDirectory\Prompt\no_human.md</c>. Checked when a session starts, not here.
    /// </param>
    public WorkflowOrchestrator(
        IPromptTemplateService prompts,
        IAutoAnswerService autoAnswer,
        IArtifactWatcherFactory watchers,
        ITaskStateStore state,
        TimeSpan debounce,
        TimeSpan pollInterval,
        Func<TimeSpan, CancellationToken, Task>? settleDelay = null,
        IExecutionGate? gate = null,
        DesktopJobRunner? desktopJobs = null,
        TimeSpan? desktopJobPollInterval = null,
        StallPolicy? stall = null,
        IDesktopJobClock? clock = null,
        bool requireDecisionApproval = true,
        string? noHumanPromptPath = null)
    {
        _prompts = prompts;
        _autoAnswer = autoAnswer;
        _watchers = watchers;
        _state = state;
        _debounce = debounce;
        _pollInterval = pollInterval;
        _settleDelay = settleDelay ?? Task.Delay;
        _gate = gate ?? new ExecutionGate();
        _desktopJobs = desktopJobs;
        _desktopJobPollInterval = desktopJobPollInterval ?? DefaultDesktopJobPollInterval;
        _stall = stall ?? StallPolicy.Default;
        _clock = clock ?? desktopJobs?.Clock ?? new SystemDesktopJobClock();
        _requireDecisionApproval = requireDecisionApproval;
        _noHumanPromptPath = noHumanPromptPath
            ?? Path.Combine(AppContext.BaseDirectory, "Prompt", NoHumanPromptFile);
    }

    /// <inheritdoc />
    public async Task RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Enum.IsDefined(request.StartPhase))
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        // The journal must never claim a phase is complete that THIS run has not run. Re-running
        // a finished task would otherwise leave the phases after Specification marked Completed
        // until the run reaches them, so a crash during Specification recovers a tab with every
        // later phase painted green; and a crash in the microsecond gap between
        // RecordPhase(N, Completed) and RecordPhase(N+1, Active) could let the next recovery skip
        // N+1 (SPEC section 7.4, D24).
        ClearPhasesFrom(request.Paths, request.StartPhase);

        try
        {
            foreach (var definition in PhaseCatalog.All.Skip((int)request.StartPhase))
            {
                // One of the two places a pause acts (Workflow_LOAD_AND_PAUSE spec 6.3): between two
                // phases. The other is between two sessions of an Implementation subtask run, in
                // RunSubtaskLoopAsync. Never before the start phase - Start/Continue is the user
                // asking for exactly that one - and never inside a session. The journal already
                // reads "N Completed, N+1 Pending" here, so a crash or a close while held recovers
                // at N+1, and the phase-N session stays live until RunPhaseAsync starts the next one.
                if (definition.Phase != request.StartPhase && request.Pause is not null)
                {
                    await request.Pause.WaitWhilePausedAsync(cancellationToken);
                }

                await RunPhaseAsync(request, definition, cancellationToken);
            }
        }
        finally
        {
            request.Terminal.DisposeSession();
        }
    }

    private void ClearPhasesFrom(TaskPaths paths, WorkflowPhase startPhase)
    {
        var existing = _state.TryLoad(paths);
        if (existing is null)
        {
            // No journal yet - nothing to correct. The first RecordPhase will create it.
            return;
        }

        // Phases BEFORE the start phase are left exactly as they are: on a resumed run their
        // green indicators are the only record that they ever happened.
        _state.ReplacePhases(
            paths,
            [.. existing.Phases.Select(entry => (int)entry.Phase < (int)startPhase
                ? entry
                : new TaskPhaseState(entry.Phase, PhaseStatus.Pending, null))]);
    }

    private async Task RunPhaseAsync(
        WorkflowRunRequest request,
        PhaseDefinition definition,
        CancellationToken cancellationToken)
    {
        // Store first: Progress.Report marshals to the UI thread, so a busy dispatcher must not
        // be able to delay the durable write (SPEC section 7.3).
        _state.RecordPhase(request.Paths, definition.Phase, PhaseStatus.Active);
        request.Progress.Report(new PhaseProgress(definition.Phase, PhaseStatus.Active));
        request.ManualSignal.Reset();

        // Implementation splits here and nowhere else. Everything above is shared, so a disabled or
        // absent configuration reaches exactly the code it reached before (requirement 2.1):
        // SubtaskConfiguration.IsEnabled is the single place that decides what "enabled" means,
        // and a null snapshot - the default of WorkflowRunRequest.Subtasks - is disabled.
        //
        // This expression is also THE capture point of requirement 1.6. `Subtasks` is a deferred
        // capture, and `?.Value` below is where it is forced; because `&&` short-circuits on the
        // phase check, a run that never reaches implementation never reads the tab's configuration
        // at all. Keep the phase test first. Forcing it here and again in
        // RunSubtaskImplementationAsync is safe by construction - Lazy<T> runs its factory at most
        // once, so both reads see the one pair taken at this moment.
        if (definition.Phase == WorkflowPhase.Implementation
            && SubtaskConfiguration.IsEnabled(request.Subtasks?.Value))
        {
            await RunSubtaskImplementationAsync(request, cancellationToken);

            // Deliberately no RecordPhase(Completed): a subtask run completes Implementation only
            // on a freshly read, all-complete final snapshot, or on the user's manual override.
            // Returning here leaves the journal at Active, which is what keeps the task
            // recoverable (design "Session and Loop", issues 6a/6b/9 resolved).
            return;
        }

        // A marker left by a previous run of this task would satisfy the simplifier's or the
        // implementation's watcher in milliseconds. Deleting it before the baseline is taken
        // removes the failure mode instead of handling it (SPEC section 8.3). It stays in the phase
        // method rather than moving into RunSessionAsync because it is phase-specific; the baseline
        // is the very first statement of that method, so "before the baseline" still holds.
        // Unlike the done marker, a report that cannot be deleted stops the run: it would complete
        // the phase at once and start Review on documents nobody simplified (SPEC section 7.1a).
        if (definition.Phase == WorkflowPhase.Simplifier)
        {
            if (!DeleteStaleFlag(request.Paths.SimplifiedAbsolute))
            {
                throw new IOException(
                    $"Der Bericht '{request.Paths.SimplifiedAbsolute}' aus einem frueheren Lauf konnte nicht "
                    + "geloescht werden (schreibgeschuetzt oder geoeffnet). Bitte freigeben und den Workflow fortsetzen.");
            }
        }
        else if (definition.Phase == WorkflowPhase.Implementation)
        {
            DeleteStaleFlag(request.Paths.DoneAbsolute);
        }

        await RunSessionAsync(
            request.Terminal,
            request.ManualSignal,
            request.Paths.WorkingDirectory,
            definition.Launcher,
            definition.PromptFile,
            PromptVariables.For(request.Paths, request.TaskDescription),
            definition.SubmissionMode,
            definition.Completion,
            request.Paths.TaskDirectory,
            WatchedPaths(request.Paths, definition),
            request.LauncherStatus,
            // A phase session has a human at the keyboard: never nudged, never killed.
            stall: null,
            afterStallKill: null,
            cancellationToken);

        _state.RecordPhase(request.Paths, definition.Phase, PhaseStatus.Completed);
        request.Progress.Report(new PhaseProgress(definition.Phase, PhaseStatus.Completed));
    }

    /// <summary>
    /// Implementation with subtask mode enabled, up to and including decomposition: re-validate the
    /// captured configuration, persist it, report the decomposing stage, reuse a readable ordered
    /// index or run exactly one decomposition session, and end with an index that can be executed.
    /// </summary>
    /// <param name="request">The run parameters; <see cref="WorkflowRunRequest.Subtasks"/> is enabled.</param>
    /// <param name="cancellationToken">Cancels the decomposition session.</param>
    /// <exception cref="SubtaskConfigurationException">
    /// The tracking directory is no longer usable, or no ordered index could be established. Both
    /// are user-fixable: the caller presents the German message and Implementation stays
    /// <see cref="PhaseStatus.Active"/>, so the task is offered again (requirements 1.8 and 2.9).
    /// </exception>
    private async Task RunSubtaskImplementationAsync(
        WorkflowRunRequest request,
        CancellationToken cancellationToken)
    {
        // Not a re-capture. The one capture was taken by the Implementation guard in RunPhaseAsync a
        // few lines up - this run's entry into Implementation - and Lazy<T> serves that same immutable record
        // here, so nothing in this method can observe the enabled flag at one moment and the
        // directory at another (requirement 1.6); SubtaskConfiguration.Capture, Deferred and
        // CaptureValidated take an ISubtaskConfiguration this method is never given. What Implementation
        // entry owes the run is the second validation - the marker can disappear between the start
        // gate and this moment - and EnsureUsable is that step, the same call CaptureValidated
        // makes after capturing (requirement 1.8).
        var configuration = request.Subtasks!.Value;
        configuration.EnsureUsable();

        // Non-null and usable by the line above, so the path set can be composed.
        var tracking = new SubtaskPaths(configuration.WorkflowDirectory!, request.Paths.TaskName);

        // Requirement 1.5: the configuration this run actually used is recorded with the task. The
        // normalised spelling is stored rather than the raw selection so the journal, the German
        // validation message and every composed path name the directory one way.
        _state.SaveSubtaskSettings(request.Paths, configuration.Enabled, tracking.WorkflowDirectory);

        // The reuse decision comes FIRST and the deletion only inside the branch it enables. A
        // readable ordered index is reused exactly as it stands (requirement 2.3), so deleting the
        // task-level result.json ahead of this test would destroy the very evidence recovery
        // depends on and re-decompose a task that is already half executed. What is deleted is
        // therefore only a *stale* flag: a file that exists, is non-empty enough to satisfy the
        // watcher's FilesExist rule on its first poll, and yet yields no order at all.
        var decomposed = false;
        if (!SubtaskLedger.IsDecomposed(tracking))
        {
            // Only a run that really decomposes says so: a reused index is reported by its counts
            // below, so a decision hold never shows "Zerlegung läuft…".
            request.SubtaskProgress?.Report(
                new SubtaskProgress(SubtaskStage.Decomposing, 0, 0, 0, null, []));

            // The watcher would create this itself; doing it here states that composing the
            // tracking task folder is the application's job and lets the delete below run against
            // an existing directory.
            Directory.CreateDirectory(tracking.TaskDirectory);
            DeleteStaleFlag(tracking.ResultAbsolute);

            await RunDecompositionSessionAsync(request, tracking, cancellationToken);
            decomposed = true;
        }

        // Requirement 2.9. Re-read from disk rather than trusting the session's outcome: the
        // manual-signal winner of RunSessionAsync is unspecified when both tasks complete at once,
        // so it is never evidence about the artefact. An index that is still unusable here is the
        // user-fixable error, not an empty run.
        if (!SubtaskLedger.IsDecomposed(tracking))
        {
            throw new SubtaskConfigurationException(
                $"Der Subtask-Index '{tracking.ResultAbsolute}' konnte nicht gelesen werden. "
                + "Erwartet wird ein JSON-Objekt mit einer nicht leeren Liste \"subtasks\" "
                + "in Ausführungsreihenfolge.");
        }

        // The index's counts, before anything can hold the run: the decision approval below may
        // wait for the user, and the indicator must show the real progress meanwhile, not a
        // Decomposing stage that is over - or never happened.
        if (SubtaskLedger.TryRead(tracking) is { } snapshot)
        {
            PublishProgress(request, snapshot, null);
        }

        // B7: nothing runs unattended on decisions the user has not approved. After decomposition,
        // because a fresh index needs fresh decisions; before the loop, because its first session
        // is the first one that acts on them.
        var sessionEnded = decomposed;
        if (_requireDecisionApproval)
        {
            sessionEnded |= await EnsureDecisionsAsync(request, tracking, decomposed, cancellationToken);

            if (ManualCompletionRequested(request.ManualSignal))
            {
                return;
            }
        }

        // A decomposition or pre-flight session that just ended is a boundary like any other, so a
        // pause pressed while it ran holds the first subtask.
        await RunSubtaskLoopAsync(request, tracking, sessionEnded, cancellationToken);

        CompleteImplementationIfEveryEntryIsComplete(request, tracking);
    }

    /// <summary>
    /// Closes the journal transition <see cref="RunPhaseAsync"/> deliberately deferred, and only on
    /// evidence read from disk after the loop has ended (requirements 3.5 and 3.6).
    /// </summary>
    /// <param name="request">The run parameters, supplying the journal key and progress channel.</param>
    /// <param name="tracking">The task's tracking paths; the read is composed from these, not handed in.</param>
    /// <remarks>
    /// <para>
    /// The rule is design decision (issues 6a and 9): a <em>freshly read, non-null</em> final
    /// snapshot with <c>Total &gt; 0 &amp;&amp; Completed == Total</c>.
    /// <see cref="SubtaskLedger.AllComplete(SubtaskPaths)"/> is that rule and nothing else - it takes
    /// the path set and performs the read itself, so it is structurally incapable of being fed the
    /// snapshot the loop last held. Re-deriving the predicate here from a
    /// <see cref="SubtaskSnapshot"/> would both duplicate the ledger's vocabulary and re-open the
    /// very door the decision closes; note 4.1's rule applies here in full, one level up from the
    /// session: the run's own outcome is never evidence about the artefact.
    /// </para>
    /// <para>
    /// Both of the loop's exits therefore land in the same place. Whether it ended because no entry
    /// remained eligible or because its own read came back null, this reads again - so an
    /// unreadable index cannot complete a task on the strength of what was true a moment earlier,
    /// and "no failures" is not enough either, because an untouched entry is
    /// <see cref="SubtaskStatus.Pending"/>, not <see cref="SubtaskStatus.Failed"/>.
    /// </para>
    /// <para>
    /// Refusing writes nothing. Implementation keeps the <see cref="PhaseStatus.Active"/> that phase
    /// entry recorded, which is exactly what leaves the task recoverable and offered again
    /// (requirement 3.6). Manual completion is not decided here at all: it is an explicit user
    /// override owned by the view model (design decision, issue 6b).
    /// </para>
    /// </remarks>
    private void CompleteImplementationIfEveryEntryIsComplete(
        WorkflowRunRequest request,
        SubtaskPaths tracking)
    {
        if (!SubtaskLedger.AllComplete(tracking))
        {
            return;
        }

        // Store before reporting, for the reason RunPhaseAsync states: a busy dispatcher must not
        // be able to delay the durable write.
        _state.RecordPhase(request.Paths, WorkflowPhase.Implementation, PhaseStatus.Completed);
        request.Progress.Report(new PhaseProgress(WorkflowPhase.Implementation, PhaseStatus.Completed));
    }

    /// <summary>
    /// Executes the index in declared order, one session at a time and at most one attempt per
    /// entry per run (requirements 2.4, 2.5, 2.7, 2.8, 2.10).
    /// </summary>
    /// <param name="request">The run parameters.</param>
    /// <param name="tracking">The task's tracking paths.</param>
    /// <param name="sessionEnded">
    /// True when an Implementation session - the decomposition - has already ended in this run, so a pause
    /// may hold even the first subtask.
    /// </param>
    /// <param name="cancellationToken">Cancels the loop and the session it is waiting on.</param>
    /// <remarks>
    /// <para>
    /// The ledger is re-read at the top of every iteration rather than being materialised once: a
    /// session writes the very files the next decision reads, and requirement 4.5 forbids a stored
    /// current-subtask cursor. The only state this run keeps in memory is
    /// <c>attempted</c> - the titles it has already started work on - and it is deliberately local
    /// to this method, so it cannot outlive the run. That set, together with the dependencies,
    /// is what <see cref="SubtaskScheduler.Plan"/> decides eligibility from (design "Session and
    /// Loop", issue 2 resolved): an entry left <see cref="SubtaskStatus.Failed"/>,
    /// <see cref="SubtaskStatus.Pending"/> or waiting by an earlier run is eligible again here once
    /// its dependencies are complete, and gets exactly one fresh attempt; no entry is ever
    /// attempted twice within one run. By default an entry depends on every previous one, so
    /// nothing after an unfinished entry is launched.
    /// </para>
    /// <para>
    /// Comparison is <see cref="StringComparer.OrdinalIgnoreCase"/> because that is precisely how
    /// the ledger de-duplicates the index (requirement 2.12): a set that compared ordinally would
    /// see two entries where the ledger reports one and could attempt the same folder twice.
    /// </para>
    /// <para>
    /// Termination: every iteration either returns or adds one title to <c>attempted</c>, and only
    /// unattempted titles are runnable, so the loop is bounded by the number of entries the index
    /// lists. The one exception is an iteration that held for a pause: it repeats the decision, but
    /// only after the user pressed Play, so it cannot spin. There is no execution timeout anywhere
    /// in it (requirement 2.8, E8); the bound is on iterations, never on how long a live session or
    /// a hold may take.
    /// </para>
    /// <para>
    /// Desktop job iterations are the other exception: they add nothing to <c>attempted</c>. A job
    /// that keeps failing is bounded by <see cref="MaxCountedDesktopJobAttemptsPerRun"/>; one that
    /// completes makes its entry runnable; one that ran and failed (<c>timeout</c>,
    /// <c>script_failed</c>) hands its entry to a repair session, bounded by
    /// <see cref="MaxVerificationSessionsPerRun"/>; and the outcomes that count nothing - a busy machine, a
    /// lock, a closed idle window - only ever repeat after a poll interval, because the run is meant
    /// to wait for the user to be away, however long that takes. A rewritten request whose next step
    /// repeats a script and limit that timed out in this run is not started at all: the entry is
    /// exhausted with its reason, checked before the idle wait and again at the launch.
    /// </para>
    /// </remarks>
    private async Task RunSubtaskLoopAsync(
        WorkflowRunRequest request,
        SubtaskPaths tracking,
        bool sessionEnded,
        CancellationToken cancellationToken)
    {
        var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Like `attempted`, local to this run: an explicit Continue starts from zero and so still
        // retries every failed entry. Reset by any Complete; see MaxConsecutiveFailuresPerRun.
        var consecutiveFailures = 0;

        // Local to this run as well: counted desktop job attempts, exhausted jobs and verification
        // or repair sessions. A new run gives every job and every session a fresh allowance.
        var jobs = new DesktopJobRunState();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (consecutiveFailures >= MaxConsecutiveFailuresPerRun)
            {
                // The failure reasons are already on the indicator's tooltip (the ledger carries
                // each failreason), so ending the run is all that is needed to make them visible
                // instead of burning one more doomed session per remaining entry.
                return;
            }

            var snapshot = SubtaskLedger.TryRead(tracking);
            if (snapshot is null)
            {
                // The index was readable when this method was entered, so it has just become
                // unreadable - most plausibly a transient lock while an agent rewrites it. The run
                // ends here rather than raising, because the read is ambiguous: TryRead returns
                // null for a momentary lock or a half-written file far more often than for a
                // tracking repository that has genuinely gone away, and requirement 2.10's
                // principle - no individual bad read invalidates the index - argues against
                // escalating that ambiguity to a fatal error. Requirement 2.9's error belongs
                // where no order could be established at all, and it still surfaces on the next
                // Continue, which re-enters through EnsureUsable and IsDecomposed. Nothing is
                // lost by returning: completed subtasks are already recorded on disk, and 2.9's
                // error would leave implementation Active too.
                return;
            }

            // The scheduler owns eligibility: index order, dependencies and the attempted set. An
            // entry it skips for an unmet dependency is NOT marked attempted, so it becomes eligible
            // in this same run the moment its dependency completes.
            var plan = SubtaskScheduler.Plan(snapshot, attempted, jobs.Exhausted);

            // A desktop job runs when the run has nothing else to do - polling for the idle window
            // until it opens - or, ahead of a runnable entry, when its window is already open: the
            // user is away now, and a session started first could keep the job waiting for hours.
            if (_desktopJobs is not null
                && plan.Kind is SubtaskPlanKind.Runnable or SubtaskPlanKind.WaitingForDesktopWindow
                && SubtaskScheduler.DesktopJobsWaiting(snapshot, jobs.Exhausted) is [var job, ..]
                && (plan.Kind == SubtaskPlanKind.WaitingForDesktopWindow || IsIdleWindowOpen(tracking, job)))
            {
                if (!await RunDesktopJobAsync(request, tracking, job, jobs, attempted, cancellationToken))
                {
                    return;
                }

                continue;
            }

            if (plan.Kind != SubtaskPlanKind.Runnable || plan.Next is not { } entry)
            {
                EndRunWithoutRunnableEntry(request, snapshot, plan);
                return;
            }

            // The second place a pause acts (spec 6.3): between two sessions of this phase. Never
            // before the first session of the run, which Start/Continue just asked for, and never
            // after the last entry - the check sits below the "nothing left" return, so a finished
            // index still completes Implementation instead of waiting for Play. The entry is not
            // yet in `attempted`, so holding does not cost it its attempt.
            if (sessionEnded && request.Pause is { IsPaused: true })
            {
                await HoldWhilePausedAsync(request, cancellationToken);

                if (ManualCompletionRequested(request.ManualSignal))
                {
                    return;
                }

                // Re-read rather than run the entry chosen before the hold: a hold can last hours,
                // and the tracking files may have changed meanwhile.
                continue;
            }

            // B7: checked before EVERY session, not only once before the loop. decisions.md may be
            // edited while a session runs, and an approval is only worth the files it was given for.
            // Like the pause above, the entry is not yet attempted and the decision is re-read.
            if (_requireDecisionApproval && !DecisionApproval.IsValid(tracking, request.Paths.TaskName))
            {
                if (!await HoldForDecisionApprovalAsync(request, tracking, cancellationToken))
                {
                    return;
                }

                continue;
            }

            // Marked before any work, so every exit below - a skip, a failure, an exception -
            // still costs this entry its one attempt for this run.
            attempted.Add(entry.Title);

            if (entry.Status == SubtaskStatus.Deferred)
            {
                // Only a deferred entry whose job delivered, or ran and failed, is runnable: this is a
                // verification or a repair.

                // Read before the session renames the result and rewrites the request. Evidence the ledger just
                // read but that cannot be confirmed now ends the entry's part of the run instead.
                if (entry.JobNeedsRepair && !jobs.RememberTimeout(entry.Title, tracking.SubtaskDirectory(entry.Title)))
                {
                    continue;
                }

                jobs.CountVerification(entry.Title);
            }

            // Published BEFORE the session, from the snapshot this iteration already read. Two
            // things depend on the position. It is what replaces the transient
            // SubtaskStage.Decomposing of a reusing run with live counts straight away, instead of
            // leaving the indicator claiming decomposition for the whole of the first session; and
            // it is the only report in which CurrentTitle names a subtask that is actually about to
            // run (requirement 3.2).
            PublishProgress(request, snapshot, entry.Title);

            var description = TryReadAttemptableDescription(tracking, entry.Title);
            if (description is null)
            {
                // Requirement 2.10: the entry fails and the run continues with the others. The
                // failure needs no recording here - the ledger derives exactly the same verdict
                // from the same files on the next read, which is what the indicator displays.
                // Not counted towards the breaker: no session ran, so there is no shared runtime
                // cause to protect later entries from, and 2.10 wants the rest attempted.
                continue;
            }

            // Only now, and only for the entry actually being attempted: a per-subtask result.json
            // left by an earlier run is non-empty, so CompletionRule.FilesExist would be satisfied
            // on the watcher's first poll and the session would end before the agent had written
            // anything (the same failure mode the task-level flag has).
            DeleteStaleFlag(tracking.SubtaskResultFile(entry.Title));

            await RunSubtaskSessionAsync(request, tracking, entry.Title, description, cancellationToken);
            sessionEnded = true;

            // The flag is on disk, but the payload beside it may still be mid-rename. Settle it
            // before anything reads it, or the very next line would publish a failure for a subtask
            // that in fact succeeded (requirement 2.11).
            await SettleSubtaskStatusAsync(tracking, entry.Title, cancellationToken);

            // Requirement 3.2: refreshed after each subtask. Re-read rather than adjusted in
            // memory - the session is what changed the files, and the ledger is the only thing that
            // interprets them. A read that fails here publishes nothing; the next iteration's read
            // decides what that means, which keeps this from being a second place that ends runs.
            var settled = SubtaskLedger.TryRead(tracking);
            if (settled is not null)
            {
                PublishProgress(request, settled, entry.Title);

                var outcome = settled.States.FirstOrDefault(
                    state => string.Equals(state.Title, entry.Title, StringComparison.OrdinalIgnoreCase));

                // Only an unambiguous verdict moves the counter; an unreadable re-read leaves it be,
                // and so do the waiting statuses - waiting is not a shared runtime cause to guard
                // against, and their dependents are held back by the scheduler instead.
                if (outcome?.Status == SubtaskStatus.Complete)
                {
                    consecutiveFailures = 0;
                }
                else if (outcome?.Status == SubtaskStatus.Failed)
                {
                    consecutiveFailures++;
                }
            }

            if (ManualCompletionRequested(request.ManualSignal))
            {
                // 'Task abschliessen' is an explicit user override (design issue 6b), so the run
                // stops here instead of starting a session per remaining entry - each of which the
                // still-armed signal would end immediately anyway.
                return;
            }
        }
    }

    /// <summary>Waits until the user presses Play or 'Task abschliessen', whichever comes first.</summary>
    /// <param name="request">The run parameters; its <see cref="WorkflowRunRequest.Pause"/> is set.</param>
    /// <param name="cancellationToken">Ends the hold with an <see cref="OperationCanceledException"/>.</param>
    /// <remarks>
    /// The manual signal is part of the race because 'Task abschliessen' stays enabled for the whole
    /// of Implementation and awaits the end of the run: a hold on the pause gate alone would leave that
    /// button hanging until Play (spec D9). The signal is re-armed once per phase and never inside
    /// the loop, so a raised signal stays raised for the caller's check. The outer await unwraps the
    /// winner, as in <see cref="RunSessionAsync"/>, so a cancellation surfaces instead of being dropped.
    /// </remarks>
    private static async Task HoldWhilePausedAsync(
        WorkflowRunRequest request,
        CancellationToken cancellationToken)
    {
        var resumed = request.Pause!.WaitWhilePausedAsync(cancellationToken); // Non-null: the caller checked IsPaused.
        var manual = request.ManualSignal.WaitAsync(cancellationToken);

        await await Task.WhenAny(resumed, manual);
    }

    /// <summary>Reports whether one deferred entry's desktop job may start now.</summary>
    /// <param name="tracking">The task's tracking paths.</param>
    /// <param name="title">A deferred entry waiting for its job.</param>
    /// <returns>
    /// True when the idle window its request asks for is open - and for an invalid request, whose
    /// <see cref="DesktopJobOutcome.StartFailed"/> needs no idle user to be recorded.
    /// </returns>
    private bool IsIdleWindowOpen(SubtaskPaths tracking, string title)
    {
        var job = DesktopJobFiles.ReadRequest(tracking.SubtaskDirectory(title)).Request;

        return job is null || IdleWindowPolicy.IsOpen(_desktopJobs!.ReadActivity(), job.IdleMinutes);
    }

    /// <summary>
    /// Waits for the idle window of one deferred entry's desktop job, then runs its next step while
    /// holding the execution gate exclusively.
    /// </summary>
    /// <param name="request">The run parameters.</param>
    /// <param name="tracking">The task's tracking paths.</param>
    /// <param name="title">The deferred entry, first in index order among those waiting.</param>
    /// <param name="jobs">This run's desktop job bookkeeping.</param>
    /// <param name="attempted">This run's attempted titles; a completed job releases its entry once.</param>
    /// <param name="cancellationToken">Ends the wait or kills the running step.</param>
    /// <returns>False when the user asked to finish the task while it waited; otherwise true.</returns>
    /// <remarks>
    /// The poll races the manual signal, so 'Task abschliessen' never waits for an idle user; a pause
    /// holds before every poll, like it holds between two sessions. The gate is taken only once the
    /// window is open: a waiting exclusive request stops every tab from starting sessions, which is
    /// right while a job is about to run and wrong while it merely waits for the user to leave.
    /// <para>
    /// A press of <see cref="WorkflowRunRequest.DesktopJobStart"/> ends the current poll and skips the
    /// idle requirement for this one step only. The wait is ended before the session is stopped, so the
    /// button is off before the job starts. On that skip path the launch point re-checks 'Task
    /// abschliessen', Pause and that the job is still the one pressed for (same request id and next
    /// step), because the session stop and the gate wait lie between the press and the launch.
    /// </para>
    /// </remarks>
    private async Task<bool> RunDesktopJobAsync(
        WorkflowRunRequest request,
        SubtaskPaths tracking,
        string title,
        DesktopJobRunState jobs,
        HashSet<string> attempted,
        CancellationToken cancellationToken)
    {
        var runner = _desktopJobs!; // Non-null: the caller checked.
        var directory = tracking.SubtaskDirectory(title);

        // The repair kept a script and limit that already timed out in this run: it could only time out again.
        if (jobs.RefuseRepeatedTimeout(title, directory))
        {
            return true;
        }

        try
        {
            // A fresh wait per call: a press made before it, or consumed by an earlier step, never carries in.
            var startNow = request.DesktopJobStart;
            var requested = startNow?.BeginWait();
            var skipIdleWait = false;
            var pressedRequestId = Guid.Empty;
            var pressedStepId = string.Empty;

            try
            {
                while (true)
                {
                    if (ManualCompletionRequested(request.ManualSignal))
                    {
                        return false;
                    }

                    if (request.Pause is { IsPaused: true })
                    {
                        await HoldWhilePausedAsync(request, cancellationToken);
                        continue;
                    }

                    var job = DesktopJobFiles.ReadRequest(directory).Request;
                    var activity = runner.ReadActivity();
                    if (job is null || IdleWindowPolicy.IsOpen(activity, job.IdleMinutes))
                    {
                        break;
                    }

                    // The press is bound to the job the status line named, so the launch point can tell
                    // whether the job it would start is still that one.
                    if (requested is { IsCompleted: true })
                    {
                        skipIdleWait = true;
                        pressedRequestId = job.RequestId;
                        pressedStepId = NextStepId(directory);
                        break;
                    }

                    request.LauncherStatus?.Report(WaitingForIdleMessage(title, activity, directory, job));
                    await WaitForDesktopJobPollAsync(request, runner, requested, cancellationToken);
                }
            }
            finally
            {
                // On every exit, and before the session stop below: the button is off before the job starts.
                startNow?.EndWait();
            }

            // A session that ended on its flag is still alive in this tab's terminal - the agent's
            // process included. It holds no gate any more, so the gate cannot keep it from running
            // next to the job: it is stopped here, and the job starts only once that is confirmed.
            // Other tabs' unattended sessions are kept out by the gate itself.
            if (!await request.Terminal.StopSessionAsync(SessionStopTimeout, cancellationToken))
            {
                throw new SubtaskConfigurationException(SessionNotStoppedDesktopJobNotStartedMessage);
            }

            DesktopJobStepReport report;
            using (await _gate.AcquireExclusiveAsync(cancellationToken))
            {
                // Again at the launch point: the idle wait can last hours, and the files may have changed meanwhile.
                if (jobs.RefuseRepeatedTimeout(title, directory))
                {
                    return true;
                }

                // Skip path only (spec D7): with the idle requirement waived, nothing else would refuse a
                // 'Task abschliessen' or Pause click made while the session stopped or the gate was awaited.
                if (skipIdleWait && ManualCompletionRequested(request.ManualSignal))
                {
                    return false;
                }

                // The press is dropped rather than held inside the gate, which would block every tab's
                // sessions for the whole pause; the re-entered wait holds while paused instead.
                if (skipIdleWait && request.Pause is { IsPaused: true })
                {
                    return true;
                }

                // A replaced request or another next step is a different job: it gets its own idle time.
                skipIdleWait = skipIdleWait && IsThePressedJob(directory, pressedRequestId, pressedStepId);

                request.LauncherStatus?.Report($"Desktop-Job läuft: {title}/{NextStepId(directory)}");
                report = await runner.RunNextStepAsync(directory, request.Paths.WorkingDirectory, skipIdleWait, cancellationToken);
            }

            jobs.Apply(title, report, attempted);

            // Nothing about the job changed, and the window is most likely still open: without a
            // pause the loop would measure the CPU or find the lock again straight away.
            if (report.Outcome is DesktopJobOutcome.AbortedBusy or DesktopJobOutcome.AbortedLocked)
            {
                await WaitForDesktopJobPollAsync(request, runner, startNow: null, cancellationToken);
            }

            return true;
        }
        finally
        {
            request.LauncherStatus?.Report(null);
        }
    }

    /// <summary>
    /// Waits one poll interval, until the user asks to finish the task, or to start the job now.
    /// </summary>
    /// <param name="request">The run parameters.</param>
    /// <param name="runner">The desktop job runner, whose clock times the poll.</param>
    /// <param name="startNow">The current wait's press, or null when no press can end this poll.</param>
    /// <param name="cancellationToken">Ends the wait with an <see cref="OperationCanceledException"/>.</param>
    private async Task WaitForDesktopJobPollAsync(
        WorkflowRunRequest request,
        DesktopJobRunner runner,
        Task? startNow,
        CancellationToken cancellationToken)
    {
        var poll = runner.Clock.DelayAsync(_desktopJobPollInterval, cancellationToken);
        var manual = request.ManualSignal.WaitAsync(cancellationToken);

        // The outer await unwraps the winner, so a cancellation still surfaces.
        await await (startNow is null
            ? Task.WhenAny(poll, manual)
            : Task.WhenAny(poll, manual, startNow.WaitAsync(cancellationToken)));
    }

    /// <summary>
    /// Whether the desktop job of <paramref name="directory"/> is still the one a start-now press was
    /// made for: the same request id and the same next step.
    /// </summary>
    private static bool IsThePressedJob(string directory, Guid requestId, string stepId) =>
        DesktopJobFiles.ReadRequest(directory).Request is { } job
        && job.RequestId == requestId
        && string.Equals(NextStepId(directory), stepId, StringComparison.Ordinal);

    /// <summary>The status line while a desktop job waits for the user to be away.</summary>
    private static string WaitingForIdleMessage(string title, UserActivity activity, string directory, DesktopJobRequest job)
    {
        var state = activity.IsLocked ? ", Sitzung gesperrt"
            : activity.IsScreenSaverRunning ? ", Bildschirmschoner aktiv"
            : string.Empty;

        return $"Desktop-Job {title} wartet auf Leerlauf (seit {(int)activity.IdleTime.TotalMinutes} min ohne Eingabe, "
            + $"{job.IdleMinutes} min nötig{state}{LastAttemptNote(directory, job)}).";
    }

    /// <summary>
    /// The suffix of the waiting message that names the job's most recent recorded attempt, or an empty
    /// string when there is none.
    /// </summary>
    /// <remarks>
    /// Reads the authoritative result copy only (never the folder copy, which the agent can write). An attempt
    /// that belongs to another request is shown, but labelled as such, and is never treated as evidence about
    /// the current one. Never throws: <see cref="DesktopJobFiles.ReadResult(string)" /> returns null for an
    /// unreadable file, and a status text must not end the wait.
    /// </remarks>
    private static string LastAttemptNote(string directory, DesktopJobRequest job)
    {
        var result = DesktopJobFiles.ReadResult(directory);
        var last = result?.Steps.OfType<DesktopJobStepResult>().MaxBy(step => step.EndedUtc);
        if (last is null)
        {
            return string.Empty;
        }

        var label = DesktopJobFiles.MatchingResult(job, result) is null ? " (vorige Anfrage)" : string.Empty;

        // Nothing ran: the runner records a pre-launch aborted_user_input with equal start and end times.
        var duration = last.Outcome is DesktopJobOutcome.AbortedBusy or DesktopJobOutcome.StartFailed
            || last.EndedUtc <= last.StartedUtc
                ? string.Empty
                : $" nach {(int)(last.EndedUtc - last.StartedUtc).TotalMinutes} min";
        var attempts = last.Attempts == 1 ? "1 Versuch" : $"{last.Attempts} Versuche";

        return $"; letzter Versuch{label}: {last.Outcome}{duration}, {attempts}";
    }

    /// <summary>The id of the step a desktop job would run next, for the status line.</summary>
    private static string NextStepId(string directory)
    {
        var job = DesktopJobFiles.ReadRequest(directory).Request;
        if (job is null)
        {
            return DesktopJobFiles.RequestFileName;
        }

        var next = DesktopJobFiles.NextStepIndex(
            directory, job, DesktopJobFiles.MatchingResult(job, DesktopJobFiles.ReadResult(directory)));

        return next < 0 ? "-" : job.Steps[next].Id;
    }

    /// <summary>
    /// One run's desktop job bookkeeping. Like <c>attempted</c>, it never outlives the run. That includes the
    /// timeout signatures it refuses to repeat.
    /// </summary>
    private sealed class DesktopJobRunState
    {
        private readonly Dictionary<string, string> _exhausted = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _verifications = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _repairs = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _timedOut = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Entries whose job used up its attempts, with the reason that ends the run.</summary>
        public IReadOnlyDictionary<string, string> Exhausted => _exhausted;

        /// <summary>
        /// Records that a session of a deferred entry (verification or repair) starts for
        /// <paramref name="title"/>; both share the <see cref="MaxVerificationSessionsPerRun"/> budget.
        /// </summary>
        public void CountVerification(string title) =>
            _verifications[title] = _verifications.GetValueOrDefault(title) + 1;

        /// <summary>
        /// Counts one step outcome and re-admits its entry, either for a job that completed or,
        /// bounded, after a repair outcome.
        /// </summary>
        /// <remarks>
        /// Attempts are counted per entry, request and step, so a session that defers again with a
        /// new request starts that job with a full allowance. A completed job removes its entry from
        /// <paramref name="attempted"/> - which is what lets the verification session run in the
        /// same run - but only while the entry has had fewer than
        /// <see cref="MaxVerificationSessionsPerRun"/> verifications.
        /// <para>
        /// A repair outcome (<see cref="DesktopJobOutcome.NeedsRepair"/>) is handled first and never
        /// counts as an attempt: counting it would end in the <c>erschöpft</c> reason, and that stale
        /// entry in the exhausted set would keep the repaired request from running in this run
        /// (anti-goal A1). Instead the entry is re-admitted for a repair session. <c>_repairs</c>
        /// bounds those re-admissions independent of the ledger: a guard report that no session
        /// follows cannot re-admit more than <see cref="MaxVerificationSessionsPerRun"/> times
        /// (spec 4.6).
        /// </para>
        /// </remarks>
        public void Apply(string title, DesktopJobStepReport report, HashSet<string> attempted)
        {
            if (DesktopJobOutcome.NeedsRepair(report.Outcome))
            {
                var repairs = _repairs.GetValueOrDefault(title) + 1;
                _repairs[title] = repairs;

                // Re-admit while both the repair count and the session count are within bounds; the
                // outcome that follows the last allowed session ends the run with its reason.
                if (repairs <= MaxVerificationSessionsPerRun
                    && _verifications.GetValueOrDefault(title) < MaxVerificationSessionsPerRun)
                {
                    attempted.Remove(title);
                }
                else
                {
                    _exhausted[title] =
                        $"Desktop-Job {title}/{report.StepId} fehlgeschlagen: {report.Outcome}; keine weitere Reparatursitzung in diesem Lauf";
                }

                return;
            }

            if (report.CountsAsAttempt)
            {
                var key = $"{title}|{report.RequestId}|{report.StepId}";
                var count = _attempts.GetValueOrDefault(key) + 1;
                _attempts[key] = count;

                if (count >= MaxCountedDesktopJobAttemptsPerRun)
                {
                    var reason = string.IsNullOrWhiteSpace(report.Reason) ? string.Empty : $" ({report.Reason})";
                    _exhausted[title] =
                        $"Desktop-Job {title}/{report.StepId ?? DesktopJobFiles.RequestFileName} erschöpft: {report.Outcome}{reason}";
                }
            }

            if (report.JobComplete && _verifications.GetValueOrDefault(title) < MaxVerificationSessionsPerRun)
            {
                attempted.Remove(title);
            }
        }

        /// <summary>
        /// Remembers the script and limit that <paramref name="title"/>'s next step timed out with, before its
        /// repair session renames the result and rewrites the request.
        /// </summary>
        /// <returns>
        /// False when the recorded repair outcome cannot be confirmed from the files; the entry is then exhausted
        /// for the run and its repair session must not start.
        /// </returns>
        public bool RememberTimeout(string title, string directory)
        {
            if (ReadNextStep(directory) is not { RepairOutcome: { } outcome, ScriptSha256: { } sha } next)
            {
                _exhausted[title] =
                    $"Desktop-Job {title}: Fehlerbefund nicht lesbar; keine Reparatursitzung in diesem Lauf";
                return false;
            }

            if (outcome == DesktopJobOutcome.Timeout)
            {
                _timedOut.Add(TimedOutKey(title, sha, next.Step.TimeoutMinutes));
            }

            return true;
        }

        /// <summary>
        /// Exhausts <paramref name="title"/> for the rest of the run when its next step would run a script with the
        /// limit it already timed out with in this run.
        /// </summary>
        /// <returns>True when the job must not start.</returns>
        public bool RefuseRepeatedTimeout(string title, string directory)
        {
            if (ReadNextStep(directory) is not { RepairOutcome: null, ScriptSha256: { } sha } next
                || !_timedOut.Contains(TimedOutKey(title, sha, next.Step.TimeoutMinutes)))
            {
                return false;
            }

            _exhausted[title] =
                $"Desktop-Job {title}/{next.Step.Id} nicht erneut gestartet: dasselbe Skript mit timeoutMinutes {next.Step.TimeoutMinutes} endete in diesem Lauf schon mit timeout";
            return true;
        }

        private static string TimedOutKey(string title, string scriptSha256, int timeoutMinutes) =>
            $"{title}|{scriptSha256}|{timeoutMinutes}";

        /// <summary>The step the job in <paramref name="directory"/> would launch next, its script hash now and its recorded repair outcome.</summary>
        /// <returns>Null without a valid request, or when every step completed.</returns>
        private static (DesktopJobStep Step, string? ScriptSha256, string? RepairOutcome)? ReadNextStep(string directory)
        {
            var request = DesktopJobFiles.ReadRequest(directory).Request;
            if (request is null)
            {
                return null;
            }

            var matching = DesktopJobFiles.MatchingResult(request, DesktopJobFiles.ReadResult(directory));
            var next = DesktopJobFiles.NextStepIndex(directory, request, matching);
            if (next < 0)
            {
                return null;
            }

            var step = request.Steps[next];
            var sha = DesktopJobFiles.ScriptSha256(directory, step.Script);

            return (step, sha, DesktopJobFiles.RepairOutcome(matching?.Steps ?? [], step, sha));
        }
    }

    /// <summary>Ends the run because the scheduler found nothing to launch.</summary>
    /// <param name="request">The run parameters; its progress channel may be absent.</param>
    /// <param name="snapshot">The snapshot the plan was made from.</param>
    /// <param name="plan">Anything but <see cref="SubtaskPlanKind.Runnable"/>.</param>
    /// <remarks>
    /// <see cref="SubtaskPlanKind.Done"/> publishes nothing: the last report already shows every entry
    /// complete, and completion itself is decided from a fresh read afterwards. The two blocked
    /// outcomes publish one closing report carrying the reason, so the indicator can show why no
    /// further session started - none is started here.
    /// </remarks>
    private static void EndRunWithoutRunnableEntry(
        WorkflowRunRequest request,
        SubtaskSnapshot snapshot,
        SubtaskPlan plan)
    {
        switch (plan.Kind)
        {
            case SubtaskPlanKind.WaitingForDesktopWindow:
                EndRunWhileDesktopJobWaits(request, snapshot, plan.Titles);
                break;

            case SubtaskPlanKind.TerminallyBlocked:
                PublishBlocked(
                    request,
                    snapshot,
                    string.Concat(
                        "Keine weiteren Subtasks ausführbar:",
                        Environment.NewLine,
                        string.Join(Environment.NewLine, plan.Reasons)));
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Deferred entries wait for a job outside the terminal, and no desktop job runner was given:
    /// the run ends like a blocked one, with its own message. With a runner the loop never gets here
    /// - it polls for the idle window and runs the job instead (<see cref="RunDesktopJobAsync"/>).
    /// </summary>
    /// <param name="request">The run parameters; its progress channel may be absent.</param>
    /// <param name="snapshot">The snapshot the plan was made from.</param>
    /// <param name="titles">The deferred entries, in index order.</param>
    private static void EndRunWhileDesktopJobWaits(
        WorkflowRunRequest request,
        SubtaskSnapshot snapshot,
        IReadOnlyList<string> titles) =>
        PublishBlocked(request, snapshot, $"Desktop-Job wartet: {string.Join(", ", titles)}");

    /// <summary>Publishes the run's closing report for a run that ended without a runnable entry.</summary>
    /// <param name="request">The run parameters; its progress channel may be absent.</param>
    /// <param name="snapshot">The snapshot that supplies the counts and the states.</param>
    /// <param name="reason">Why the run ended; reaches the indicator tooltip.</param>
    private static void PublishBlocked(WorkflowRunRequest request, SubtaskSnapshot snapshot, string reason)
    {
        request.SubtaskProgress?.Report(
            new SubtaskProgress(
                SubtaskStage.Finished,
                snapshot.Completed,
                snapshot.Total,
                snapshot.Failed,
                null,
                snapshot.States,
                snapshot.Waiting,
                snapshot.Deferred,
                reason));
    }

    /// <summary>Runs one subtask session and waits for that subtask's own completion flag.</summary>
    /// <param name="request">The run parameters.</param>
    /// <param name="tracking">The task's tracking paths, supplying the absolute prompt tokens.</param>
    /// <param name="title">The entry being attempted; a safe title resolving to its own folder.</param>
    /// <param name="description">The full text of that entry's <c>subtask.md</c> (requirement 2.5).</param>
    /// <param name="cancellationToken">Cancels the session.</param>
    /// <remarks>
    /// Like every other session it runs in the <em>product</em> working directory - subtasks change
    /// product code - and reaches the tracking repository only through the absolute tokens. Its
    /// completion evidence is <em>this</em> subtask's <c>result.json</c>, so the watcher is aimed at
    /// that subtask's folder: watching the task directory would let a sibling's flag, or the
    /// task-level index, end the wrong session.
    /// </remarks>
    private async Task RunSubtaskSessionAsync(
        WorkflowRunRequest request,
        SubtaskPaths tracking,
        string title,
        string description,
        CancellationToken cancellationToken)
    {
        // Discarded for the reason its <returns> documents: Task.WhenAny leaves its winner
        // unspecified in a tie, so a false value would not prove the user had not signalled. The
        // loop asks the signal itself instead, which is exact in both directions.
        _ = await RunSessionAsync(
            request.Terminal,
            request.ManualSignal,
            request.Paths.WorkingDirectory,
            UnattendedLauncher(SubtaskRunLauncher),
            SubtaskRunPromptFile,
            PromptVariables.ForSubtaskRun(
                request.Paths, request.TaskDescription, tracking, title, description),
            SubtaskRunSubmissionMode,
            CompletionRule.FilesExist,
            tracking.SubtaskDirectory(title),
            [tracking.SubtaskResultFile(title)],
            request.LauncherStatus,
            _stall,
            () => WriteStalledOutcome(tracking, title),
            cancellationToken);
    }

    /// <summary>
    /// Records a subtask whose session the watchdog killed as failed, the way the session itself
    /// would have: <c>status.json</c> first, then the non-empty flag, both by temporary file and
    /// rename (B6).
    /// </summary>
    /// <param name="tracking">The task's tracking paths.</param>
    /// <param name="title">The entry whose session was killed.</param>
    /// <remarks>
    /// The one place the application writes a subtask's status itself, and it is called only after
    /// the session's process tree is gone, so no agent can write the same files at the same time.
    /// Fields of a payload the agent already left are kept; the outcome fields are overwritten and
    /// any field of the session schema that is missing gets its default.
    /// </remarks>
    private void WriteStalledOutcome(SubtaskPaths tracking, string title)
    {
        var statusPath = tracking.SubtaskStatusFile(title);
        var status = TryReadJsonObject(statusPath) ?? new JsonObject();

        status["subtask"] = title;
        status["status"] = "failed";
        status["failreason"] = $"stalled: keine Terminalausgabe seit {(int)_stall.StallAfter.TotalMinutes} min";
        Default("openFindings", 0);
        Default("workflowRelevantFindings", new JsonArray());
        Default("testsPassed", false);
        Default("readyForVerification", false);

        WriteAtomically(statusPath, status.ToJsonString(StalledOutcomeWriteOptions));
        WriteAtomically(
            tracking.SubtaskResultFile(title),
            new JsonObject { ["subtask"] = title, ["status"] = "failed" }.ToJsonString(StalledOutcomeWriteOptions));

        void Default(string name, JsonNode value)
        {
            if (!status.ContainsKey(name))
            {
                status[name] = value;
            }
        }
    }

    /// <summary>Reads a JSON object, or null when the file is absent, unreadable or not an object.</summary>
    private static JsonObject? TryReadJsonObject(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path), documentOptions: StatusDocumentOptions) as JsonObject
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes a file through a temporary file and a rename, as the session protocol requires.</summary>
    private static void WriteAtomically(string path, string content)
    {
        var temporary = path + ".tmp";

        File.WriteAllText(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// The launcher of an unattended session: questions are switched off and the no-human system
    /// prompt is appended (B6).
    /// </summary>
    /// <param name="launcher">The bare launcher, <c>yo</c>.</param>
    /// <returns>The launcher with both flags.</returns>
    /// <exception cref="PromptTemplateException">
    /// The no-human prompt is missing, or its path holds a character the shell or cmd would interpret.
    /// </exception>
    /// <remarks>
    /// <c>--disallowedTools</c> is variadic, so it stands before the next <c>--</c> flag, which ends
    /// its list. The path is quoted for the spaces of <c>Program Files</c>; <c>yo.bat</c> passes it on
    /// through cmd, and the session types the line into pwsh, so a character either of them would
    /// expand is refused rather than escaped.
    /// </remarks>
    private string UnattendedLauncher(string launcher)
    {
        var path = _noHumanPromptPath;

        if (!File.Exists(path))
        {
            throw new PromptTemplateException(
                $"Die Prompt-Datei '{NoHumanPromptFile}' wurde nicht gefunden: {path}");
        }

        if (path.IndexOfAny(['&', '|', '<', '>', '^', '%', '"', '$', '`']) >= 0)
        {
            throw new PromptTemplateException(
                $"Der Pfad der Prompt-Datei '{NoHumanPromptFile}' enthält ein Zeichen, das die Shell auswertet: {path}");
        }

        return $"{launcher} --disallowedTools AskUserQuestion --append-system-prompt-file \"{path}\"";
    }

    /// <summary>
    /// The decision pre-flight (B7): makes sure the task has a <c>decisions.md</c> the user has
    /// approved for exactly this index, before any subtask session runs.
    /// </summary>
    /// <param name="request">The run parameters.</param>
    /// <param name="tracking">The task's tracking paths.</param>
    /// <param name="decomposed">True when this run has just written a new index.</param>
    /// <param name="cancellationToken">Cancels the session and the hold.</param>
    /// <returns>True when a pre-flight session ran.</returns>
    /// <exception cref="SubtaskConfigurationException">The pre-flight session left no <c>decisions.md</c>.</exception>
    /// <remarks>
    /// A missing <c>decisions.md</c>, or a new index, regenerates the decisions; any earlier approval
    /// and any earlier <c>decisions.md</c> are deleted first, the latter so the watcher's
    /// <see cref="CompletionRule.FilesExist"/> cannot be met by the old file. A <c>decisions.md</c>
    /// that exists - written by hand, or by an earlier run - is never regenerated: it only needs the
    /// approval.
    /// </remarks>
    private async Task<bool> EnsureDecisionsAsync(
        WorkflowRunRequest request,
        SubtaskPaths tracking,
        bool decomposed,
        CancellationToken cancellationToken)
    {
        var sessionRan = false;

        if (decomposed || !DecisionApproval.HasDecisions(tracking))
        {
            DeleteStaleFlag(DecisionApproval.ApprovalFile(tracking));
            DeleteStaleFlag(DecisionApproval.DecisionsFile(tracking));

            await RunDecisionsSessionAsync(request, tracking, cancellationToken);
            sessionRan = true;

            if (ManualCompletionRequested(request.ManualSignal))
            {
                return sessionRan;
            }

            // Re-read from disk for the reason RunSubtaskImplementationAsync gives for the index.
            if (!DecisionApproval.HasDecisions(tracking))
            {
                throw new SubtaskConfigurationException(
                    $"Die Entscheidungsdatei '{DecisionApproval.DecisionsFile(tracking)}' wurde nicht erzeugt. "
                    + "Lege sie an oder setze den Workflow fort, um sie erneut erzeugen zu lassen.");
            }
        }

        if (!DecisionApproval.IsValid(tracking, request.Paths.TaskName))
        {
            await HoldForDecisionApprovalAsync(request, tracking, cancellationToken);
        }

        return sessionRan;
    }

    /// <summary>
    /// Holds the run on the pause gate until the user approves the decisions with Play, then records
    /// the approval for exactly the hashes the hold showed - unless the files changed meanwhile, in
    /// which case nothing is approved and the hold is shown again.
    /// </summary>
    /// <param name="request">The run parameters.</param>
    /// <param name="tracking">The task's tracking paths.</param>
    /// <param name="cancellationToken">Ends the hold with an <see cref="OperationCanceledException"/>.</param>
    /// <returns>False when the user asked to finish the task instead; nothing is approved then.</returns>
    /// <exception cref="SubtaskConfigurationException">The run has no pause gate, so there is no Play to approve with.</exception>
    /// <remarks>
    /// The run pauses the gate itself - the tab follows through <see cref="PhasePauseGate.Paused"/> and
    /// offers Play - and holds exactly like a pause between two sessions. A Play while
    /// <c>decisions.md</c> is missing approves nothing and holds again.
    /// </remarks>
    private async Task<bool> HoldForDecisionApprovalAsync(
        WorkflowRunRequest request,
        SubtaskPaths tracking,
        CancellationToken cancellationToken)
    {
        var pause = request.Pause
            ?? throw new SubtaskConfigurationException(
                $"Die Entscheidungen in '{DecisionApproval.DecisionsFile(tracking)}' sind nicht freigegeben, "
                + "und dieser Lauf kann nicht auf eine Freigabe warten.");

        var changedSinceShown = false;

        while (!DecisionApproval.IsValid(tracking, request.Paths.TaskName))
        {
            // What the user is shown is what Play approves - never the files as they are at Play.
            var shown = DecisionApproval.Capture(tracking);

            pause.Pause();
            request.LauncherStatus?.Report(AwaitApprovalMessage(shown, changedSinceShown));

            try
            {
                await HoldWhilePausedAsync(request, cancellationToken);
            }
            finally
            {
                request.LauncherStatus?.Report(null);
            }

            if (ManualCompletionRequested(request.ManualSignal))
            {
                return false;
            }

            // Changed between the hold being shown and Play: nothing is approved, the hold returns
            // with the new hash and says why.
            changedSinceShown = shown is not null
                && !DecisionApproval.TryApprove(tracking, request.Paths.TaskName, shown, _clock.UtcNow);
        }

        return true;
    }

    /// <summary>The hold's status line, naming the first hex digits of the decisions it shows.</summary>
    /// <param name="shown">The hashes shown; null while <c>decisions.md</c> or the index is missing.</param>
    /// <param name="changedSinceShown">True when the previous hold's Play met changed files.</param>
    private static string AwaitApprovalMessage(DecisionSnapshot? shown, bool changedSinceShown)
    {
        var message = shown is null
            ? DecisionsAwaitApprovalMessage
            : $"Entscheidungen prüfen (decisions.md {shown.ShortDecisionsHash}…): freigeben mit Play";

        return changedSinceShown ? $"{DecisionsChangedMessage}. {message}" : message;
    }

    /// <summary>Runs the one pre-flight session that writes the task's <c>decisions.md</c>.</summary>
    /// <param name="request">The run parameters.</param>
    /// <param name="tracking">The task's tracking paths, supplying the absolute prompt tokens.</param>
    /// <param name="cancellationToken">Cancels the session.</param>
    /// <remarks>
    /// Shaped like <see cref="RunDecompositionSessionAsync"/>: the product working directory, the
    /// decomposition token set, and a watcher on the tracking task folder whose evidence is a
    /// non-empty <c>decisions.md</c>. A stalled session is simply killed; the missing file is then
    /// the caller's error.
    /// </remarks>
    private async Task RunDecisionsSessionAsync(
        WorkflowRunRequest request,
        SubtaskPaths tracking,
        CancellationToken cancellationToken)
    {
        // Discarded for the reason RunDecompositionSessionAsync gives.
        _ = await RunSessionAsync(
            request.Terminal,
            request.ManualSignal,
            request.Paths.WorkingDirectory,
            UnattendedLauncher(DecisionsLauncher),
            DecisionsPromptFile,
            PromptVariables.ForSubtaskCreation(request.Paths, request.TaskDescription, tracking),
            DecisionsSubmissionMode,
            CompletionRule.FilesExist,
            tracking.TaskDirectory,
            [DecisionApproval.DecisionsFile(tracking)],
            request.LauncherStatus,
            _stall,
            afterStallKill: null,
            cancellationToken);
    }

    /// <summary>
    /// Waits, boundedly, until one attempted subtask's status payload can be read and parsed.
    /// </summary>
    /// <param name="tracking">The task's tracking paths.</param>
    /// <param name="title">The entry whose session has just published its completion flag.</param>
    /// <param name="cancellationToken">Cancels the wait between two reads.</param>
    /// <remarks>
    /// <para>
    /// Requirement 2.11 and design decision (issue 10). The agent publishes <c>status.json</c> and
    /// then its completion flag, both by temporary-file rename, so the moment the flag is observed
    /// the payload beside it may still be a half-written or momentarily locked file. This method
    /// retries that single read at <see cref="StatusSettleInterval"/>, at most
    /// <see cref="StatusSettleRetries"/> times.
    /// </para>
    /// <para>
    /// The predicate is a successful <em>parse</em>, deliberately not "the state is no longer
    /// Pending". The latter would return on the first read of a payload that legitimately still says
    /// <c>pending</c> - design issue 1 makes that a valid, settled outcome - and would go on waiting
    /// for a state change that a finished-but-unsuccessful session is never going to make.
    /// </para>
    /// <para>
    /// An <em>absent</em> payload settles immediately rather than burning the retries: E7 and design
    /// issue 1 say a missing status after a flag is Pending, not Failed, so there is nothing for a
    /// retry to change - the file was written before the flag if it was written at all.
    /// </para>
    /// <para>
    /// Nothing is written and nothing is returned. Exhausted retries leave the payload exactly as it
    /// is on disk, and <see cref="SubtaskLedger"/> - the only thing that interprets tracking files -
    /// derives <see cref="SubtaskStatus.Failed"/> from an unreadable or malformed one. Keeping the
    /// verdict there is what stops this method from becoming a second, disagreeing interpreter.
    /// </para>
    /// </remarks>
    private async Task SettleSubtaskStatusAsync(
        SubtaskPaths tracking,
        string title,
        CancellationToken cancellationToken)
    {
        var path = tracking.SubtaskStatusFile(title);

        for (var retry = 0; ; retry++)
        {
            if (StatusPayloadIsSettled(path))
            {
                return;
            }

            if (retry >= StatusSettleRetries)
            {
                // Exhausted. The entry stays whatever the files say it is, which for a payload that
                // is still unusable is Failed, carrying the ledger's reason.
                return;
            }

            await _settleDelay(StatusSettleInterval, cancellationToken);
        }
    }

    /// <summary>Reports whether one status payload has stopped being a moving target.</summary>
    /// <param name="path">Absolute path of that subtask's <c>status.json</c>.</param>
    /// <returns>
    /// True when the file is absent, or when it could be read and parsed as JSON; false while it is
    /// locked, unreadable or not yet valid JSON.
    /// </returns>
    /// <remarks>
    /// The <em>meaning</em> of the parsed document is not inspected at all. Which value settles a
    /// subtask is the ledger's decision, and duplicating the closed status vocabulary here would
    /// create a second place that could disagree with it about what <c>complete</c> means.
    /// </remarks>
    private static bool StatusPayloadIsSettled(string path)
    {
        string content;
        try
        {
            if (!File.Exists(path))
            {
                return true;
            }

            content = File.ReadAllText(path);
        }
        catch (IOException)
        {
            // The publishing session still holds it. This is the case the retries exist for.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        try
        {
            // Parsed purely to establish that it parses; the document itself is of no interest here.
            JsonDocument.Parse(content, StatusDocumentOptions).Dispose();

            return true;
        }
        catch (JsonException)
        {
            // Half-written: the rename has not landed yet, or the file is genuinely malformed. One
            // more read at the settling interval tells the two apart.
            return false;
        }
    }

    /// <summary>Publishes one refreshed progress report for the running loop.</summary>
    /// <param name="request">The run parameters; its progress channel may be absent.</param>
    /// <param name="snapshot">A freshly read snapshot, which supplies the counts and the states.</param>
    /// <param name="currentTitle">The entry this report is about.</param>
    /// <remarks>
    /// The counts are taken from the snapshot rather than recomputed, so the <c>{N} von {M}</c> the
    /// indicator shows and the states its tooltip renders can never come from two different reads
    /// (requirements 3.2 and 3.7). <see cref="SubtaskSnapshot.States"/> is passed straight through:
    /// it is already an ordered read-only list, and it is the channel design issue 7 chose for the
    /// failure reasons, so a failed entry's <c>failreason</c> reaches the tooltip without a second
    /// lookup.
    /// </remarks>
    private static void PublishProgress(
        WorkflowRunRequest request,
        SubtaskSnapshot snapshot,
        string? currentTitle)
    {
        request.SubtaskProgress?.Report(
            new SubtaskProgress(
                SubtaskStage.Running,
                snapshot.Completed,
                snapshot.Total,
                snapshot.Failed,
                currentTitle,
                snapshot.States,
                snapshot.Waiting,
                snapshot.Deferred));
    }

    /// <summary>
    /// Reads the description of an entry that may be attempted, or reports that it may not be.
    /// </summary>
    /// <param name="tracking">The task's tracking paths.</param>
    /// <param name="title">The title exactly as the index listed it.</param>
    /// <returns>
    /// The full description text, or <see langword="null"/> when this entry is a failure that the
    /// loop skips: an unsafe title, a title without its own folder, or a missing, empty or
    /// unreadable <c>subtask.md</c>.
    /// </returns>
    /// <remarks>
    /// Requirement 2.10 is honoured by the order of the checks: the two path tests are pure string
    /// work and run <em>before</em> any file is touched, so an unsafe title never reaches the
    /// filesystem at all - and the one read that does happen is composed by
    /// <see cref="SubtaskPaths"/> and therefore always inside the tracking repository.
    /// </remarks>
    private static string? TryReadAttemptableDescription(SubtaskPaths tracking, string title)
    {
        if (!SubtaskPaths.IsValidTitle(title) || !ResolvesToItsOwnFolder(tracking, title))
        {
            return null;
        }

        var path = tracking.SubtaskMarkdown(title);

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var text = File.ReadAllText(path);

            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Reports whether a title gets a folder of its own on this filesystem.</summary>
    /// <param name="tracking">The task's tracking paths.</param>
    /// <param name="title">A title that already satisfies <see cref="SubtaskPaths.IsValidTitle"/>.</param>
    /// <returns>True when the composed path still ends in the title itself.</returns>
    /// <remarks>
    /// <para>
    /// Requirement 2.10 rejects only the exact dot-names <c>.</c> and <c>..</c>, so a title ending
    /// in a dot is valid - but Windows strips trailing dots while resolving the path, so
    /// <c>trailing.</c> resolves to the sibling entry <c>trailing</c> in <em>every</em> path
    /// composed from it: its description, its status payload and its completion flag are that
    /// sibling's files. Attempting it would therefore delete the sibling's flag, paste the
    /// sibling's description a second time, and hand the agent a status path that overwrites the
    /// sibling's evidence - while the ledger, which de-duplicates by title string and never by
    /// resolved path, went on reporting the two entries as independent.
    /// </para>
    /// <para>
    /// The collapse is not uniform, which is why the check is made on the folder rather than on any
    /// one file: <c>trailing..</c> and <c>...</c> collapse in the <em>directory</em> path - the
    /// session's watch root, which the watcher would then create and observe on the sibling - while
    /// the file paths composed below them are left alone. One test on the folder covers all of
    /// them, and none of the collapsing forms stray outside the tracking repository.
    /// </para>
    /// <para>
    /// Such an entry is therefore skipped as a failure exactly like an unsafe title. It has no
    /// distinct identity on disk, so there is nothing it could be attempted <em>as</em>; refusing
    /// it costs one entry, while attempting it silently corrupts another entry's result - and the
    /// ledger, which de-duplicates by title string and never by resolved path, would go on
    /// reporting both entries as if they were independent.
    /// </para>
    /// <para>
    /// <see cref="Path.GetFullPath(string)"/> performs the collapse as pure string work and touches
    /// no file, which is what keeps this check compatible with "without accessing anything outside
    /// the tracking repository".
    /// </para>
    /// </remarks>
    private static bool ResolvesToItsOwnFolder(SubtaskPaths tracking, string title) =>
        string.Equals(
            Path.GetFileName(Path.GetFullPath(tracking.SubtaskDirectory(title))),
            title.Trim(),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Reports whether the user has asked to finish the task, without waiting.</summary>
    /// <param name="manualSignal">The run's manual signal.</param>
    /// <returns>True once <see cref="ManualPhaseSignal.Signal"/> has been called.</returns>
    /// <remarks>
    /// The signal is the authority, not the <c>manualWon</c> flag of a finished session: that flag
    /// is derived from <see cref="Task.WhenAny(Task, Task)"/>, whose winner is unspecified when
    /// both tasks are already complete, so it can report the watcher for a run the user did signal.
    /// Asking the signal is exact in both directions and costs nothing - an uncancellable wait on
    /// an already-completed source is the source's own task, so this only inspects its state.
    /// </remarks>
    private static bool ManualCompletionRequested(ManualPhaseSignal manualSignal) =>
        manualSignal.WaitAsync(CancellationToken.None).IsCompleted;

    /// <summary>Runs the one decomposition session that turns the existing spec and plan into an index.</summary>
    /// <param name="request">The run parameters.</param>
    /// <param name="tracking">The task's tracking paths, supplying the absolute prompt tokens.</param>
    /// <param name="cancellationToken">Cancels the session.</param>
    /// <remarks>
    /// The session runs in the <em>product</em> working directory, like every other session: the
    /// prompt's own <c>cd</c> goes there because the sources it reads are working-directory
    /// relative, and the tracking repository is reached only through the absolute tokens
    /// <see cref="PromptVariables.ForSubtaskCreation"/> substitutes. Its completion evidence is the
    /// task-level <c>result.json</c>, which is both the ordered index and the decomposition flag,
    /// so the watcher is aimed at the tracking task folder rather than at
    /// <see cref="TaskPaths.TaskDirectory"/> - the reason the watch root is a parameter of the
    /// shared session primitive at all.
    /// </remarks>
    private async Task RunDecompositionSessionAsync(
        WorkflowRunRequest request,
        SubtaskPaths tracking,
        CancellationToken cancellationToken)
    {
        // The returned manualWon is deliberately discarded: Task.WhenAny's winner is unspecified
        // when both tasks are already complete, so it can neither prove nor disprove that the
        // index arrived. The caller re-reads the index from disk instead. Manual completion during
        // a subtask run is recorded by the manual-override path, not here.
        _ = await RunSessionAsync(
            request.Terminal,
            request.ManualSignal,
            request.Paths.WorkingDirectory,
            UnattendedLauncher(DecompositionLauncher),
            DecompositionPromptFile,
            PromptVariables.ForSubtaskCreation(request.Paths, request.TaskDescription, tracking),
            DecompositionSubmissionMode,
            CompletionRule.FilesExist,
            tracking.TaskDirectory,
            [tracking.ResultAbsolute],
            request.LauncherStatus,
            _stall,
            afterStallKill: null,
            cancellationToken);
    }

    /// <summary>
    /// Runs one agent session end to end: artefact baseline, a fresh terminal at the product
    /// working directory, the launcher, auto-answer, the rendered prompt, and then the race
    /// between the artefact condition and the user's manual completion. This is the single body
    /// every phase run shares. The caller keeps the journal transitions, the progress reports,
    /// the manual-signal reset and any phase-specific preparation.
    /// </summary>
    /// <param name="terminal">The terminal this session drives.</param>
    /// <param name="manualSignal">The user's escape hatch out of this session.</param>
    /// <param name="workingDirectory">The product working directory the session runs in.</param>
    /// <param name="launcher">Command typed into the shell to start the CLI.</param>
    /// <param name="promptFile">File name inside the Prompt directory.</param>
    /// <param name="substitutions">Substitution dictionary for that prompt file.</param>
    /// <param name="submissionMode">How this launcher's prompt submission is verified.</param>
    /// <param name="completion">Completion rule the watcher applies.</param>
    /// <param name="watchDirectory">
    /// Root the watcher observes. It is a parameter rather than something derived inside, because
    /// a session that is not a phase run watches a different root than the task directory;
    /// deriving it here would hard-code the phase case into the shared primitive.
    /// </param>
    /// <param name="watchedPaths">Artefact paths the completion rule applies to.</param>
    /// <param name="launcherStatus">
    /// Receives the reason while this session waits for a launcher, and the warning when its prompt
    /// submission could not be confirmed. Cleared on every exit of the session.
    /// </param>
    /// <param name="stall">
    /// Null for a session with a human at the keyboard, which then also takes no hold on the
    /// execution gate. Otherwise the session holds the gate shared and a watchdog races the two
    /// waits: the first stall gets an Escape and <see cref="StallNudgePrompt"/>, the second stops the
    /// session with a confirmed stop.
    /// </param>
    /// <param name="afterStallKill">
    /// Runs once a stalled session's process tree is gone and its waits have settled - the only point
    /// at which the application may write what the agent would have written. Null writes nothing.
    /// </param>
    /// <param name="cancellationToken">Cancels the session.</param>
    /// <returns>
    /// True when the manual signal ended the session, false when the artefact watcher did. This
    /// is information the previous inline code discarded; it changes nothing about the wait.
    /// <para>
    /// <em>Not a tie-breaker.</em> <see cref="Task.WhenAny(Task, Task)"/> leaves its winner
    /// unspecified when both tasks are already complete, so a false value is no evidence that the
    /// artefact condition failed to hold, and a true value is no evidence that it did not. A caller
    /// that needs to know what the session produced must re-read the artefact state from disk.
    /// </para>
    /// </returns>
    private async Task<bool> RunSessionAsync(
        ITerminalController terminal,
        ManualPhaseSignal manualSignal,
        string workingDirectory,
        string launcher,
        string promptFile,
        IReadOnlyDictionary<string, string> substitutions,
        PromptSubmissionMode submissionMode,
        CompletionRule completion,
        string watchDirectory,
        IReadOnlyList<string> watchedPaths,
        IProgress<string?>? launcherStatus,
        StallPolicy? stall,
        Action? afterStallKill,
        CancellationToken cancellationToken)
    {
        // Every unattended session of every tab holds the execution gate shared, for its whole
        // length, so a desktop job - which holds it exclusively - never runs next to one. Taken
        // before the baseline: a job that ran meanwhile may change exactly the files the watcher
        // compares. A phase session (stall: null) has a human in the loop and takes no hold at all:
        // a human at the desk means the idle window is closed anyway, and a hold kept for the
        // hours such a session can stay open would starve every desktop job of every tab.
        using var hold = stall is null ? null : await AcquireSessionHoldAsync(launcherStatus, cancellationToken);

        // The baseline for AnyContentChanged must be captured before the CLI can touch anything.
        using var watcher = _watchers.Create(
            completion,
            watchDirectory,
            watchedPaths,
            _debounce,
            _pollInterval);

        try
        {
            terminal.ClearScreen();
            terminal.StartSession(
                ShellLocator.FindShellExecutable(),
                ShellLocator.ShellArguments,
                workingDirectory);

            // The page must be listening before anything is written to it, or `clear`, the first
            // PTY bytes and the first snapshot are all dropped - and the launcher's startup screen
            // is exactly what the auto-answer rules need to match (spec section 6.4).
            await terminal.WaitUntilReadyAsync(cancellationToken);

            // Always quoted: working directories contain spaces and umlauts.
            terminal.Send($"cd \"{workingDirectory}\"\r");

            // Unguessable per session, and never derived from anything the prompt or the working
            // directory contains, so no rendered text can be mistaken for the launcher's own markers.
            var nonce = Guid.NewGuid().ToString("N");
            terminal.Send(ComposeLauncherCommand(launcher, nonce));

            var mayPaste = await SettleAndAnswerAsync(
                terminal,
                manualSignal,
                nonce,
                launcherStatus,
                cancellationToken);

            // Asked again here rather than trusting the value alone: the settle loop may have returned
            // a moment before the user pressed 'Task abschliessen', and starting an agent after the
            // user has asked to stop is worse than skipping a prompt (design issue 6b).
            if (mayPaste && !ManualCompletionRequested(manualSignal))
            {
                await SendPromptAsync(
                    terminal,
                    manualSignal,
                    _prompts.Render(promptFile, substitutions),
                    submissionMode,
                    launcherStatus,
                    cancellationToken);
            }

            // Every phase now has an artefact condition; the manual signal is the escape hatch for
            // every phase, not a completion rule of its own. Both run on the session's own token, so a
            // stalled session can be stopped without cancelling the run.
            using var work = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var watched = watcher.WaitAsync(work.Token);
            var manual = manualSignal.WaitAsync(work.Token);

            // The third racer exists only for an unattended session. It completes only on the
            // second stall; every other ending cancels it below.
            var watchdog = stall is null
                ? null
                : WatchForStallAsync(terminal, manualSignal, submissionMode, launcherStatus, stall, work.Token);

            try
            {
                var winner = watchdog is null
                    ? await Task.WhenAny(watched, manual)
                    : await Task.WhenAny(watched, manual, watchdog);

                if (watchdog is not null && ReferenceEquals(winner, watchdog) && watchdog.IsCompletedSuccessfully)
                {
                    // B6, and the order is the point: (1) the session's waits are cancelled, (2) the
                    // process tree is killed and its end confirmed, (3) the waits have settled - and
                    // only (4) then does the application write the outcome, when no agent process is
                    // left to write it too. An unconfirmed stop writes nothing and ends the run.
                    await work.CancelAsync();
                    var stopped = await terminal.StopSessionAsync(SessionStopTimeout, cancellationToken);
                    await SettleQuietlyAsync(watched, manual);

                    if (!stopped)
                    {
                        throw new SubtaskConfigurationException(SessionNotStoppedStatusNotWrittenMessage);
                    }

                    afterStallKill?.Invoke();

                    return false;
                }

                // This second await is the outer half of the original `await await Task.WhenAny(...)`
                // and must stay: it unwraps the winning task so a watcher failure surfaces as
                // ArtifactWatchException instead of being silently dropped. It is awaited BEFORE the
                // winner is reported, so a faulted winner throws and this method returns nothing at all.
                await winner;

                return ReferenceEquals(winner, manual);
            }
            finally
            {
                // The losers must not outlive the session: a watchdog left running would nudge the
                // next session's terminal.
                await work.CancelAsync();
                await SettleQuietlyAsync(watchdog is null ? [watched, manual] : [watched, manual, watchdog]);
            }
        }
        finally
        {
            // A warning about this session's prompt describes nothing once the session is over -
            // whether it ended on its artefacts, the user's signal, cancellation or an exception.
            launcherStatus?.Report(null);
        }
    }

    /// <summary>
    /// Watches an unattended session for silence (B6): nudges it on the first stall and completes on
    /// the second, which is the caller's cue to kill it.
    /// </summary>
    /// <param name="terminal">The session's terminal; its last output is the only liveness signal.</param>
    /// <param name="manualSignal">The user's escape hatch, handed to the nudge's prompt path.</param>
    /// <param name="submissionMode">How the session's launcher takes a prompt.</param>
    /// <param name="launcherStatus">Receives the nudge's submission warning, as for any prompt.</param>
    /// <param name="stall">The threshold and the poll interval.</param>
    /// <param name="cancellationToken">The session's work token; ends the watch with every other outcome.</param>
    /// <returns>A task that completes only when the session has stalled a second time.</returns>
    /// <remarks>
    /// A stall is <c>clock - LastOutputUtc &gt;= StallAfter</c>. After the nudge the timer restarts at the
    /// nudge itself, so the second stall needs a full <see cref="StallPolicy.StallAfter"/> of silence
    /// after it, whether or not the nudge's own echo reached the terminal. The Escape interrupts
    /// whatever the agent is waiting for - a question, a dialog - before the nudge is pasted through
    /// the same path as the session's prompt.
    /// </remarks>
    private async Task WatchForStallAsync(
        ITerminalController terminal,
        ManualPhaseSignal manualSignal,
        PromptSubmissionMode submissionMode,
        IProgress<string?>? launcherStatus,
        StallPolicy stall,
        CancellationToken cancellationToken)
    {
        var nudged = false;
        var quietSince = DateTimeOffset.MinValue;

        while (true)
        {
            await _clock.DelayAsync(stall.PollInterval, cancellationToken);

            var lastActivity = terminal.LastOutputUtc > quietSince ? terminal.LastOutputUtc : quietSince;
            if (_clock.UtcNow - lastActivity < stall.StallAfter)
            {
                continue;
            }

            if (nudged)
            {
                return;
            }

            // The timer restarts as the nudge begins, so the few seconds its submission takes count
            // towards the second stall rather than being lost to it.
            nudged = true;
            quietSince = _clock.UtcNow;

            terminal.Send("\u001b");

            // One quiet period for the launcher to redraw after the interrupt: the evidence path
            // refuses to paste while the screen still shows the working footer it had before.
            await Task.Delay(TimeSpan.FromMilliseconds(_autoAnswer.RuleSet.QuietPeriodMs), cancellationToken);
            await SendPromptAsync(terminal, manualSignal, StallNudgePrompt, submissionMode, launcherStatus, cancellationToken);
        }
    }

    /// <summary>Waits until every task has finished, whatever its outcome, and observes any exception.</summary>
    /// <remarks>
    /// For the losers of a session's race: their outcome is of no interest once the winner is known,
    /// but they must have stopped before the session counts as over.
    /// </remarks>
    private static async Task SettleQuietlyAsync(params Task[] tasks)
    {
        var all = Task.WhenAll(tasks);

        // Awaiting WhenAny never throws; reading Exception afterwards marks it observed.
        await Task.WhenAny(all);
        _ = all.Exception;
    }

    /// <summary>Takes the shared hold a session needs, saying so while a desktop job delays it.</summary>
    /// <param name="launcherStatus">Receives the waiting message, and null once the hold is taken.</param>
    /// <param name="cancellationToken">Ends the wait.</param>
    /// <returns>The hold; disposing it releases it.</returns>
    private async Task<IDisposable> AcquireSessionHoldAsync(
        IProgress<string?>? launcherStatus,
        CancellationToken cancellationToken)
    {
        var pending = _gate.AcquireSharedAsync(cancellationToken);
        if (pending.IsCompleted)
        {
            return await pending;
        }

        launcherStatus?.Report(SessionWaitsForDesktopJobMessage);
        try
        {
            return await pending;
        }
        finally
        {
            launcherStatus?.Report(null);
        }
    }

    // Keyed on the phase, not the completion rule: several phases share a rule but each watches
    // its own artefacts.
    private static IReadOnlyList<string> WatchedPaths(TaskPaths paths, PhaseDefinition definition) =>
        definition.Phase switch
        {
            WorkflowPhase.Specification => [paths.SpecAbsolute, paths.PlanAbsolute],
            WorkflowPhase.Simplifier => [paths.SimplifiedAbsolute],
            WorkflowPhase.Review => [paths.ReviewAbsolute],
            WorkflowPhase.ResolveReview => [paths.SpecAbsolute, paths.PlanAbsolute],
            WorkflowPhase.Implementation => [paths.DoneAbsolute],
            _ => [],
        };

    /// <summary>Removes a completion flag left behind by an earlier run, if one is there.</summary>
    /// <param name="path">
    /// The flag to delete: the task's done marker on the normal path, or the task-level
    /// <c>result.json</c> before a decomposition session. Never a per-subtask flag - that deletion
    /// belongs to the execution loop, which owns which entry it is attempting.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the flag is gone - deleted, or never there; <see langword="false"/>
    /// when it could not be deleted.
    /// </returns>
    /// <remarks>
    /// A missing file is not an error, so the absent case needs no probe. Failing to delete is left
    /// to the caller: the done marker's session then completes as soon as it starts, and the user
    /// still has a live terminal and the manual completion action; the simplifier refuses to start.
    /// </remarks>
    private static bool DeleteStaleFlag(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            // Held open by another process.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Read-only or no permission.
            return false;
        }
    }

    /// <summary>Pastes the rendered prompt and submits it the way this launcher's mode requires.</summary>
    /// <param name="terminal">The session's terminal.</param>
    /// <param name="manualSignal">The user's escape hatch; it stops every wait on the evidence path.</param>
    /// <param name="prompt">The rendered prompt.</param>
    /// <param name="submissionMode">The launcher's explicit submission contract.</param>
    /// <param name="launcherStatus">Receives the warning when a submission cannot be confirmed.</param>
    /// <param name="cancellationToken">Cancels the submit.</param>
    /// <remarks>
    /// Both conditions select an evidence path: Claude Code submits once on positive evidence, Codex
    /// retries until evidence. Either evidence mode falls back to today's loop when the rules file has
    /// no <see cref="AutoAnswerRuleSet.SubmittedPattern"/> - an older override, most test fixtures.
    /// The Review fallback always warns: nothing in such a file can confirm a Codex submit, because
    /// Codex redraws on every Enter and the loop's output check is no evidence for it.
    /// </remarks>
    private async Task SendPromptAsync(
        ITerminalController terminal,
        ManualPhaseSignal manualSignal,
        string prompt,
        PromptSubmissionMode submissionMode,
        IProgress<string?>? launcherStatus,
        CancellationToken cancellationToken)
    {
        if (submissionMode == PromptSubmissionMode.RetryUntilEvidence
            && !string.IsNullOrWhiteSpace(_autoAnswer.RuleSet.SubmittedPattern))
        {
            await SendPromptUntilEvidenceAsync(terminal, manualSignal, prompt, launcherStatus, cancellationToken);
            return;
        }

        if (submissionMode == PromptSubmissionMode.PositiveEvidence
            && !string.IsNullOrWhiteSpace(_autoAnswer.RuleSet.SubmittedPattern))
        {
            await SendPromptWithEvidenceAsync(terminal, manualSignal, prompt, launcherStatus, cancellationToken);
            return;
        }

        await SendPromptByOutputActivityAsync(terminal, prompt, cancellationToken);

        // Unconditional: the loop returns on output after an Enter, which Codex produces whether or
        // not it submitted, so its return says nothing about the review prompt.
        if (submissionMode == PromptSubmissionMode.RetryUntilEvidence)
        {
            launcherStatus?.Report(PromptSubmissionUnconfirmedMessage);
        }
    }

    /// <summary>
    /// Pastes the prompt and writes at most one Enter, accepting nothing but the launcher's working
    /// footer as proof that it was submitted.
    /// </summary>
    /// <param name="terminal">The session's terminal.</param>
    /// <param name="manualSignal">The user's escape hatch; checked before every write and during every wait.</param>
    /// <param name="prompt">The rendered prompt.</param>
    /// <param name="launcherStatus">Receives the warning when the submission cannot be confirmed.</param>
    /// <param name="cancellationToken">Cancels the submit.</param>
    /// <remarks>
    /// <para>
    /// Claude Code reads a stdin chunk as the Return key only when the chunk is a single carriage
    /// return. Carriage returns written while a large paste is still on its way through conhost queue
    /// up behind it, arrive together, and are inserted as literal line breaks - the Workflow_LoadFix
    /// ST-001 failure of 2026-10-06, where the repeated Enters of the output-activity loop left the
    /// chip unsent or the input box holding nothing but empty lines. Output after an Enter is no
    /// acknowledgement of that Enter either: a late paste redraw looks exactly the same.
    /// </para>
    /// <para>
    /// Hence: no Enter until the paste has demonstrably landed, a grace wait instead of probing with
    /// further Enters, never a second Enter, and a visible warning whenever the submission cannot be
    /// confirmed (SPEC section 6.5).
    /// </para>
    /// </remarks>
    private async Task SendPromptWithEvidenceAsync(
        ITerminalController terminal,
        ManualPhaseSignal manualSignal,
        string prompt,
        IProgress<string?>? launcherStatus,
        CancellationToken cancellationToken)
    {
        // Already working: no false-to-true transition could ever be attributed to this prompt.
        var baseline = await terminal.SnapshotAsync(SnapshotLines, cancellationToken);
        if (ManualCompletionRequested(manualSignal))
        {
            return;
        }

        if (_autoAnswer.IsSubmitted(baseline))
        {
            launcherStatus?.Report(PromptSubmissionUnconfirmedMessage);
            return;
        }

        // Taken BEFORE the call: the launcher can redraw before SendPaste has even returned.
        var outputBeforePaste = terminal.OutputCount;
        terminal.SendPaste(prompt);

        var landed = await WaitForPasteToLandAsync(terminal, manualSignal, outputBeforePaste, cancellationToken);
        if (ManualCompletionRequested(manualSignal))
        {
            return;
        }

        if (!landed)
        {
            launcherStatus?.Report(PromptSubmissionUnconfirmedMessage);
            return;
        }

        await WaitUntilManualOrDelayAsync(
            manualSignal,
            TimeSpan.FromMilliseconds(_autoAnswer.RuleSet.SubmitAfterPasteLandedMs),
            cancellationToken);

        var beforeEnter = await terminal.SnapshotAsync(SnapshotLines, cancellationToken);
        if (ManualCompletionRequested(manualSignal))
        {
            return;
        }

        // A match already - another query, or the prompt's own text on screen - would make a later
        // match meaningless, so the Enter is not written at all.
        if (_autoAnswer.IsSubmitted(beforeEnter))
        {
            launcherStatus?.Report(PromptSubmissionUnconfirmedMessage);
            return;
        }

        terminal.Send("\r");

        var submitted = await WaitForSubmittedAsync(terminal, manualSignal, cancellationToken);
        if (submitted || ManualCompletionRequested(manualSignal))
        {
            return;
        }

        launcherStatus?.Report(PromptSubmissionUnconfirmedMessage);
    }

    /// <summary>
    /// Pastes the prompt into an idle Codex and writes one Enter per attempt until the screen proves
    /// the prompt was taken: the working footer appears, or the collapsed-paste placeholder leaves
    /// the composer.
    /// </summary>
    /// <param name="terminal">The session's terminal.</param>
    /// <param name="manualSignal">The user's escape hatch; checked before every write and during every wait.</param>
    /// <param name="prompt">The rendered prompt.</param>
    /// <param name="launcherStatus">Receives the warning when the submission cannot be confirmed.</param>
    /// <param name="cancellationToken">Cancels the submit.</param>
    /// <remarks>
    /// <para>
    /// Output after an Enter proves nothing for Codex: it redraws on every Enter, whether it submits
    /// or not (spec 3.3 F3, 3.4 R1). The output-activity loop therefore stopped after the first Enter
    /// that only redrew and left the review prompt unsent. Only the screen counts here, and only a
    /// frame that shows Codex itself - a snapshot is lossy (blank when the page is unavailable, the
    /// last <see cref="SnapshotLines"/> lines only), so a missing match is never evidence alone.
    /// </para>
    /// <para>
    /// Retrying is safe for Codex, unlike for Claude Code. Consecutive Enters are at least
    /// <see cref="AutoAnswerRuleSet.SubmitVerifyMs"/> apart, far outside Codex's 120 ms paste-burst
    /// window, so a retry never merges into the paste. The screen is re-read before each retry, and
    /// evidence that arrived after the previous wait ends the loop without another Enter. An evidence
    /// missed all the same means the next Enter meets an empty composer, which Codex ignores.
    /// </para>
    /// <para>
    /// Fails closed: a return marker, or a screen that never showed Codex, gets the warning and no
    /// paste; a paste that never lands gets the warning and no Enter.
    /// </para>
    /// </remarks>
    private async Task SendPromptUntilEvidenceAsync(
        ITerminalController terminal,
        ManualPhaseSignal manualSignal,
        string prompt,
        IProgress<string?>? launcherStatus,
        CancellationToken cancellationToken)
    {
        var configuration = _autoAnswer.RuleSet;

        var (idle, screen) = await WaitUntilNotWorkingAsync(terminal, manualSignal, cancellationToken);
        if (ManualCompletionRequested(manualSignal))
        {
            return;
        }

        // ShowsLauncher is false on a return marker as well as on a blank page, a dialog or a shell.
        // A Codex whose startup footer never cleared is still Codex: it queues an Enter during
        // startup, and the placeholder stays usable evidence, so that case goes on without the footer.
        if (!idle && !ShowsLauncher(screen))
        {
            launcherStatus?.Report(PromptSubmissionUnconfirmedMessage);
            return;
        }

        // Taken BEFORE the call: the launcher can redraw before SendPaste has even returned.
        var outputBeforePaste = terminal.OutputCount;
        terminal.SendPaste(prompt);

        var landed = await WaitForPasteToLandAsync(terminal, manualSignal, outputBeforePaste, cancellationToken);
        if (ManualCompletionRequested(manualSignal))
        {
            return;
        }

        if (!landed)
        {
            launcherStatus?.Report(PromptSubmissionUnconfirmedMessage);
            return;
        }

        // The burst has flushed and the redraw is over; Codex's Enter-suppress window is long closed.
        await WaitUntilManualOrDelayAsync(
            manualSignal,
            TimeSpan.FromMilliseconds(configuration.PasteQuietPeriodMs),
            cancellationToken);

        var snapshot = await terminal.SnapshotAsync(SnapshotLines, cancellationToken);
        if (ManualCompletionRequested(manualSignal))
        {
            return;
        }

        var chipShown = _autoAnswer.IsPastePending(snapshot);
        var previousArmed = false;

        for (var attempt = 0; attempt < configuration.MaxSubmitAttempts; attempt++)
        {
            if (attempt > 0)
            {
                snapshot = await terminal.SnapshotAsync(SnapshotLines, cancellationToken);
            }

            var beforeEnter = snapshot;
            if (ManualCompletionRequested(manualSignal))
            {
                return;
            }

            // The previous Enter's evidence arrived after its wait: it was taken, so no further Enter.
            if (attempt > 0 && IsTaken(beforeEnter, previousArmed, chipShown))
            {
                return;
            }

            if (HasReturnMarker(beforeEnter))
            {
                break;
            }

            // The footer counts only for an Enter it was demonstrably absent before, on a frame that
            // was actually read.
            var armed = idle && !string.IsNullOrWhiteSpace(beforeEnter) && !_autoAnswer.IsSubmitted(beforeEnter);

            // Nothing could prove this Enter: today's single Enter, then the warning.
            if (!armed && !chipShown)
            {
                if (attempt == 0)
                {
                    terminal.Send("\r");
                }

                break;
            }

            terminal.Send("\r");

            var taken = await WaitForEvidenceAsync(terminal, manualSignal, armed, chipShown, cancellationToken);
            if (taken || ManualCompletionRequested(manualSignal))
            {
                return;
            }

            previousArmed = armed;
        }

        launcherStatus?.Report(PromptSubmissionUnconfirmedMessage);
    }

    /// <summary>Waits, boundedly, until Codex shows its composer without the working footer.</summary>
    /// <param name="terminal">The session's terminal.</param>
    /// <param name="manualSignal">Ends the wait when the user completes the phase.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>
    /// <c>Idle</c> true with the last frame after two consecutive idle frames; false with the last
    /// frame on a return marker, on manual completion, or when
    /// <see cref="AutoAnswerRuleSet.PasteSettleTimeoutMs"/> passes first.
    /// </returns>
    /// <remarks>
    /// Codex's startup footer ("Starting MCP servers ... esc to interrupt") is the same footer it
    /// works with, so a footer that is still on screen when the prompt is pasted could never be
    /// attributed to the Enter. Two consecutive frames, because one frame can be read mid-redraw.
    /// </remarks>
    private async Task<(bool Idle, string Screen)> WaitUntilNotWorkingAsync(
        ITerminalController terminal,
        ManualPhaseSignal manualSignal,
        CancellationToken cancellationToken)
    {
        var cap = TimeSpan.FromMilliseconds(_autoAnswer.RuleSet.PasteSettleTimeoutMs);
        var elapsed = Stopwatch.StartNew();
        var screen = string.Empty;
        var idleFrames = 0;

        while (elapsed.Elapsed < cap)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ManualCompletionRequested(manualSignal))
            {
                return (false, screen);
            }

            screen = await terminal.SnapshotAsync(SnapshotLines, cancellationToken);
            if (HasReturnMarker(screen))
            {
                return (false, screen);
            }

            idleFrames = ShowsLauncher(screen) && !_autoAnswer.IsSubmitted(screen) ? idleFrames + 1 : 0;
            if (idleFrames >= 2)
            {
                return (true, screen);
            }

            await Task.Delay(EvidencePollInterval, cancellationToken);
        }

        return (false, screen);
    }

    /// <summary>Looks, boundedly, for proof that the Enter just written was taken.</summary>
    /// <param name="terminal">The session's terminal.</param>
    /// <param name="manualSignal">Ends the wait when the user completes the phase.</param>
    /// <param name="footerArmed">Whether the working footer was absent before this Enter.</param>
    /// <param name="chipShown">Whether the collapsed-paste placeholder was on screen after the paste.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>
    /// True once <see cref="IsTaken"/> holds; false when <see cref="AutoAnswerRuleSet.SubmitVerifyMs"/>
    /// passes first or the user completed the phase.
    /// </returns>
    private async Task<bool> WaitForEvidenceAsync(
        ITerminalController terminal,
        ManualPhaseSignal manualSignal,
        bool footerArmed,
        bool chipShown,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMilliseconds(_autoAnswer.RuleSet.SubmitVerifyMs);
        var elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ManualCompletionRequested(manualSignal))
            {
                return false;
            }

            var screen = await terminal.SnapshotAsync(SnapshotLines, cancellationToken);
            if (IsTaken(screen, footerArmed, chipShown))
            {
                return true;
            }

            await Task.Delay(EvidencePollInterval, cancellationToken);
        }

        return false;
    }

    /// <summary>Whether <paramref name="screen"/> proves that Codex took the prompt.</summary>
    /// <param name="screen">The frame to judge.</param>
    /// <param name="footerArmed">Whether the working footer was absent before the Enter in question.</param>
    /// <param name="chipShown">Whether the collapsed-paste placeholder was on screen after the paste.</param>
    /// <returns>
    /// True on a Codex frame that shows the working footer (when armed) or no longer shows the
    /// placeholder (when one was shown).
    /// </returns>
    /// <remarks>
    /// The placeholder's absence counts only on a frame that shows Codex itself: a blank snapshot, a
    /// marker-only page or plain scrollback lacks the placeholder without anything having been taken.
    /// </remarks>
    private bool IsTaken(string screen, bool footerArmed, bool chipShown) =>
        ShowsLauncher(screen)
        && ((footerArmed && _autoAnswer.IsSubmitted(screen))
            || (chipShown && !_autoAnswer.IsPastePending(screen)));

    /// <summary>Whether <paramref name="screen"/> shows the running launcher's own footer.</summary>
    /// <param name="screen">The frame to judge.</param>
    /// <returns>
    /// True on a non-blank frame without a return marker that matches the ready or the submitted
    /// pattern.
    /// </returns>
    /// <remarks>
    /// A footer above a return marker is a leftover the shell did not clear, not a running launcher.
    /// </remarks>
    private bool ShowsLauncher(string screen) =>
        !string.IsNullOrWhiteSpace(screen)
        && !HasReturnMarker(screen)
        && (_autoAnswer.IsLauncherReady(screen) || _autoAnswer.IsSubmitted(screen));

    /// <summary>Whether <paramref name="screen"/> carries any return marker.</summary>
    /// <param name="screen">The frame to judge.</param>
    /// <returns>True when a line starts with <see cref="ReturnMarkerPrefix"/>.</returns>
    /// <remarks>
    /// Any nonce will do: the page is reset for every session, so a return marker on it means this
    /// session's launcher has exited to the shell.
    /// </remarks>
    private static bool HasReturnMarker(string screen) =>
        ContainsMarker(screen, ReturnMarkerPrefix, string.Empty);

    /// <summary>Waits, boundedly, until the paste has demonstrably reached the launcher.</summary>
    /// <param name="terminal">The session's terminal.</param>
    /// <param name="manualSignal">Ends the wait when the user completes the phase.</param>
    /// <param name="outputBeforePaste">The terminal's output count taken just before the paste.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>
    /// True on a collapsed-paste chip, or on output since the paste followed by a quiet period;
    /// false when <see cref="AutoAnswerRuleSet.PasteSettleTimeoutMs"/> passes without either, or the
    /// user completed the phase.
    /// </returns>
    private async Task<bool> WaitForPasteToLandAsync(
        ITerminalController terminal,
        ManualPhaseSignal manualSignal,
        long outputBeforePaste,
        CancellationToken cancellationToken)
    {
        var quietPeriod = TimeSpan.FromMilliseconds(_autoAnswer.RuleSet.PasteQuietPeriodMs);
        var cap = TimeSpan.FromMilliseconds(_autoAnswer.RuleSet.PasteSettleTimeoutMs);
        var elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < cap)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ManualCompletionRequested(manualSignal))
            {
                return false;
            }

            var screen = await terminal.SnapshotAsync(SnapshotLines, cancellationToken);

            // The chip proves the bracketed paste arrived. A short prompt is never collapsed, so its
            // own redraw, once it has stopped, is the evidence instead. Silence alone never is: it is
            // also exactly what a paste still on its way looks like.
            if (_autoAnswer.IsPastePending(screen)
                || (terminal.OutputCount != outputBeforePaste
                    && DateTimeOffset.UtcNow - terminal.LastOutputUtc >= quietPeriod))
            {
                return true;
            }

            await Task.Delay(EvidencePollInterval, cancellationToken);
        }

        return false;
    }

    /// <summary>Waits out <paramref name="delay"/> unless the user completes the phase first.</summary>
    /// <param name="manualSignal">Ends the wait early when raised.</param>
    /// <param name="delay">The wait.</param>
    /// <param name="cancellationToken">Ends the wait with an <see cref="OperationCanceledException"/>.</param>
    private static async Task WaitUntilManualOrDelayAsync(
        ManualPhaseSignal manualSignal,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        var elapsed = Task.Delay(delay, cancellationToken);
        var manual = manualSignal.WaitAsync(cancellationToken);

        // Unwrapped, as in RunSessionAsync, so a cancellation surfaces instead of being dropped.
        await await Task.WhenAny(elapsed, manual);
    }

    /// <summary>Looks, boundedly, for the launcher's working footer after the one Enter.</summary>
    /// <param name="terminal">The session's terminal.</param>
    /// <param name="manualSignal">Ends the wait when the user completes the phase.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>True once <see cref="AutoAnswerRuleSet.SubmittedPattern"/> matches.</returns>
    private async Task<bool> WaitForSubmittedAsync(
        ITerminalController terminal,
        ManualPhaseSignal manualSignal,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMilliseconds(_autoAnswer.RuleSet.SubmittedVerifyTimeoutMs);
        var elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ManualCompletionRequested(manualSignal))
            {
                return false;
            }

            var screen = await terminal.SnapshotAsync(SnapshotLines, cancellationToken);
            if (_autoAnswer.IsSubmitted(screen))
            {
                return true;
            }

            await Task.Delay(EvidencePollInterval, cancellationToken);
        }

        return false;
    }

    // The prompt used to be submitted by writing the paste block and then a carriage return after
    // a fixed 150 ms. Claude Code's TUI coalesces a paste, so a CR arriving while a multi-kilobyte
    // prompt is still draining is taken as a literal newline instead of as submit - which is why
    // the user had to press Enter by hand in every phase. Waiting for the screen to go quiet and
    // then verifying that the CR produced output removes the dependency on any fixed delay.
    // Since 2026-10-06 this is the fallback for any rules file without a submitted pattern; Codex has
    // used SendPromptUntilEvidenceAsync since WF_REVIEWENTER. Its repeated Enters are exactly what
    // Claude Code turns into literal line breaks, which is why Claude sessions take
    // SendPromptWithEvidenceAsync instead.
    private async Task SendPromptByOutputActivityAsync(
        ITerminalController terminal,
        string prompt,
        CancellationToken cancellationToken)
    {
        var configuration = _autoAnswer.RuleSet;

        terminal.SendPaste(prompt);

        // The baseline the quiet gate measures from. It MUST start here and not at
        // terminal.LastOutputUtc: SettleAndAnswerAsync returns precisely because the screen has
        // been quiet for QuietPeriodMs (1500 ms shipped), so LastOutputUtc is already older than
        // PasteQuietPeriodMs on entry. Reading it alone satisfies the gate on the first iteration
        // and writes the CR before the PTY has even echoed the paste - the exact early-submit
        // failure this method exists to remove (SPEC section 9.3).
        var quietSince = DateTimeOffset.UtcNow;

        var quietPeriod = TimeSpan.FromMilliseconds(configuration.PasteQuietPeriodMs);
        var ceiling = Stopwatch.StartNew();

        while (ceiling.Elapsed < TimeSpan.FromMilliseconds(configuration.PasteSettleTimeoutMs))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Output arriving after the paste pushes the baseline forward, so a slow launcher and
            // a chatty one are handled by the same expression.
            if (terminal.LastOutputUtc > quietSince)
            {
                quietSince = terminal.LastOutputUtc;
            }

            if (DateTimeOffset.UtcNow - quietSince >= quietPeriod)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }

        // Whether the paste is visibly sitting in the input box as a collapsed chip. When it is,
        // the only reliable proof of submission is that chip disappearing - a single carriage
        // return after a bracketed paste is taken as a newline, not submit, in the
        // pwsh -> yo -> Claude chain, and it still produces render output, so an output-count tick
        // is a false positive. We therefore press Enter until the chip is gone (spec section 9.3).
        var settled = await terminal.SnapshotAsync(SnapshotLines, cancellationToken);
        var pastePending = _autoAnswer.IsPastePending(settled);

        for (var attempt = 0; attempt < configuration.MaxSubmitAttempts; attempt++)
        {
            var before = terminal.OutputCount;

            terminal.Send("\r");
            await Task.Delay(TimeSpan.FromMilliseconds(configuration.SubmitVerifyMs), cancellationToken);

            var screen = await terminal.SnapshotAsync(SnapshotLines, cancellationToken);

            if (pastePending)
            {
                // Screen-based verification: the prompt is submitted once the collapsed-paste chip
                // is no longer on screen. Claude Code holds a ~7 s grace after a large bracketed
                // paste during which Enter is absorbed rather than submitting, so several carriage
                // returns may be needed - the loop stops the moment the chip clears.
                if (!_autoAnswer.IsPastePending(screen))
                {
                    return;
                }
            }
            else if (terminal.OutputCount != before)
            {
                // No collapsed chip (a short prompt, or no pattern configured): fall back to the
                // original output-based check.
                return;
            }
        }

        // Every attempt failed. The prompt is in the input box and the terminal is live: the user
        // can press Enter, exactly as before this fix. Better than blocking.
    }

    /// <summary>Wraps the launcher so the shell reports when it starts and when it returns.</summary>
    /// <remarks>
    /// <c>try</c>/<c>finally</c> rather than <c>;</c> on purpose: the session runs pwsh without
    /// <c>-NoProfile</c>, so a user profile may set <c>$ErrorActionPreference = 'Stop'</c>, and a
    /// command-not-found would then abort the whole statement list before a trailing statement could
    /// run - leaving the very case this exists to detect undetectable. The marker is emitted as a
    /// concatenation so the command line the terminal echoes contains only
    /// <c>'WF-RET-' + '...'</c>, never the joined literal that matching looks for.
    /// </remarks>
    private static string ComposeLauncherCommand(string launcher, string nonce) =>
        $"Write-Host ('{RunMarkerPrefix}' + '{nonce}'); " +
        $"try {{ {launcher} }} finally {{ Write-Host ('{ReturnMarkerPrefix}' + '{nonce}') }}\r";

    /// <summary>Whether one of this session's markers stands at the start of a rendered line.</summary>
    /// <remarks>
    /// Anchored to the line start so the echoed command - which carries the quoted, unjoined halves
    /// and is preceded by the shell's own prompt - can never be mistaken for the launcher's output,
    /// however the 120-column buffer wraps it.
    /// </remarks>
    private static bool ContainsMarker(string screenText, string prefix, string nonce)
    {
        if (string.IsNullOrEmpty(screenText))
        {
            return false;
        }

        var marker = prefix + nonce;

        foreach (var line in screenText.Split('\n'))
        {
            if (line.TrimStart().StartsWith(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The single definition of "the launcher's input is ready for the prompt", shared by the settle
    /// loop and by the wait that follows a stall so the two can never drift apart.
    /// </summary>
    /// <remarks>
    /// A bare ready-pattern hit is never enough: a footer fragment can be painted while the launcher
    /// is still cold-starting and its composer is not yet listening. The output and quiet-period
    /// conditions are as much a part of readiness as the pattern is.
    /// </remarks>
    private bool IsReadyToPaste(ITerminalController terminal, string screenText) =>
        terminal.OutputCount > 0
        && DateTimeOffset.UtcNow - terminal.LastOutputUtc >= TimeSpan.FromMilliseconds(_autoAnswer.RuleSet.QuietPeriodMs)
        && !string.IsNullOrWhiteSpace(screenText)
        && _autoAnswer.IsLauncherReady(screenText);

    /// <summary>
    /// Holds a session that has no running launcher, without a ceiling, until the user starts one by
    /// hand or completes the phase. The terminal stays live throughout (requirements 2.8 and 5.2).
    /// </summary>
    /// <returns>True when the launcher came up and the prompt may now be pasted.</returns>
    private async Task<bool> WaitForLauncherAsync(
        ITerminalController terminal,
        ManualPhaseSignal manualSignal,
        string nonce,
        IProgress<string?>? launcherStatus,
        CancellationToken cancellationToken)
    {
        launcherStatus?.Report(LauncherStalledMessage);

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Asked before the screen is read, so a user who has given up is never overruled by
                // whatever happens to be rendered at that moment.
                if (ManualCompletionRequested(manualSignal))
                {
                    return false;
                }

                var screen = await terminal.SnapshotAsync(SnapshotLines, cancellationToken);

                // The return marker must be gone as well as the launcher ready. A relaunched TUI
                // takes the alternate screen buffer and clears it; a ready footer left over from
                // the session that already exited does not, and pasting on that alone would put the
                // prompt back into the shell this wait exists to keep it out of.
                if (!ContainsMarker(screen, ReturnMarkerPrefix, nonce) && IsReadyToPaste(terminal, screen))
                {
                    return true;
                }

                await Task.Delay(StallPollInterval, cancellationToken);
            }
        }
        finally
        {
            // Every exit - ready, manual, cancellation - clears this session's own status and
            // nothing else, so an unrelated message on the tab survives the stall.
            launcherStatus?.Report(null);
        }
    }

    /// <summary>Answers the launcher's startup dialogs and decides whether the prompt may be pasted.</summary>
    /// <returns>
    /// True when there is positive evidence that a launcher is running and may receive the prompt;
    /// false when the user completed the phase by hand, or when the launcher is provably not
    /// running and the wait ended without one.
    /// </returns>
    private async Task<bool> SettleAndAnswerAsync(
        ITerminalController terminal,
        ManualPhaseSignal manualSignal,
        string nonce,
        IProgress<string?>? launcherStatus,
        CancellationToken cancellationToken)
    {
        var configuration = _autoAnswer.RuleSet;
        var fired = new HashSet<string>(StringComparer.Ordinal);
        var quietPeriod = TimeSpan.FromMilliseconds(configuration.QuietPeriodMs);
        var overall = Stopwatch.StartNew();

        // Latched: the run marker scrolls away the moment a full-screen TUI takes over, so "was it
        // ever seen" is the question, not "is it on screen now".
        var sawRunMarker = false;

        while (overall.Elapsed < TimeSpan.FromMilliseconds(configuration.SettleTimeoutMs))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The user's explicit override outranks every other consideration, and is observed from
            // the first iteration - not only once the ceiling is reached, which would ignore a click
            // for up to SettleTimeoutMs.
            if (ManualCompletionRequested(manualSignal))
            {
                return false;
            }

            // --- Gate A: the launcher must actually have rendered something. ----------------
            // Without this, the first iteration of a fresh session finds an "idle" terminal
            // (nothing has been written yet), snapshots an EMPTY xterm buffer, matches no rule,
            // and sends the prompt straight into the still-blocking bypass-permissions dialog -
            // whose default option is "No, exit". Quietness only means anything once the
            // launcher has drawn. See spec section 7.3.
            if (terminal.OutputCount == 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
                continue;
            }

            // --- Gate B: the screen must then stop changing. --------------------------------
            if (DateTimeOffset.UtcNow - terminal.LastOutputUtc < quietPeriod)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                continue;
            }

            var screen = await terminal.SnapshotAsync(SnapshotLines, cancellationToken);

            // A quiet terminal that still renders nothing has not finished painting. Treat it as
            // "not ready yet", not as "settled with nothing to answer".
            if (string.IsNullOrWhiteSpace(screen))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                continue;
            }

            sawRunMarker |= ContainsMarker(screen, RunMarkerPrefix, nonce);

            // Checked on every wake-up, not only at the ceiling: a launcher that fails immediately
            // returns to the shell within a second or two, and making the user wait out
            // SettleTimeoutMs before being told so is needless.
            if (ContainsMarker(screen, ReturnMarkerPrefix, nonce))
            {
                return await WaitForLauncherAsync(terminal, manualSignal, nonce, launcherStatus, cancellationToken);
            }

            var rule = _autoAnswer.Match(screen, fired);

            if (rule is null || fired.Count >= configuration.MaxAnswersPerPhase)
            {
                // Nothing left to answer. But a quiet screen is NOT proof the launcher is ready:
                // right after `yo` is typed there is a quiet window while Node/Claude cold-starts
                // during which the screen still shows the shell prompt. Pasting the prompt then
                // sends it into the shell (or into Claude before its input is listening) and it is
                // lost, so the Enter that follows has nothing to submit. Only proceed once the
                // launcher's own input is on screen (spec section 7.3).
                if (IsReadyToPaste(terminal, screen))
                {
                    return true;
                }

                // Not ready yet: keep waiting for the ready marker (or the next dialog) rather
                // than returning. The overall ceiling still bounds this.
                await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
                continue;
            }

            // One write per key press. Ink parses each stdin chunk as a single keypress, so
            // "<Down><Enter>" written at once is an unknown key that the dialog ignores - and the
            // prompt's own Enter then confirms the preselected "No, exit".
            foreach (var key in KeySequence.Split(rule.Send))
            {
                terminal.Send(key);
                await Task.Delay(KeyPressGap, cancellationToken);
            }

            fired.Add(rule.Id);

            await Task.Delay(quietPeriod, cancellationToken);
        }

        // Ceiling reached: readiness was never confirmed. What happens next is decided by evidence,
        // never by the absence of it - this is the branch that used to paste unconditionally and so
        // wrote a whole markdown prompt into a PowerShell prompt, line by line, as commands.
        if (ManualCompletionRequested(manualSignal))
        {
            return false;
        }

        var final = await terminal.SnapshotAsync(SnapshotLines, cancellationToken);
        sawRunMarker |= ContainsMarker(final, RunMarkerPrefix, nonce);

        // Either the launcher has already returned, or the shell never even ran the line we typed.
        // Neither state may receive the prompt.
        if (ContainsMarker(final, ReturnMarkerPrefix, nonce) || !sawRunMarker)
        {
            return await WaitForLauncherAsync(terminal, manualSignal, nonce, launcherStatus, cancellationToken);
        }

        // The run marker was seen and no return marker followed it: a launcher is provably running
        // and only its TUI is unrecognised - a wording change in a CLI we do not control. Pasting is
        // correct here, and keeps a cosmetic drift from stalling every run.
        return true;
    }
}
