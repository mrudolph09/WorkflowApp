using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Workflow.Models;
using Workflow.Terminal;

namespace Workflow.Services;

/// <inheritdoc cref="IWorkflowOrchestrator" />
public sealed class WorkflowOrchestrator : IWorkflowOrchestrator
{
    private const int SnapshotLines = 60;

    /// <summary>Prompt file that turns the existing spec and plan into the ordered subtask index.</summary>
    /// <remarks>
    /// Named here rather than taken from <see cref="PhaseCatalog"/>: decomposition is not a phase,
    /// and <see cref="PromptTemplateCatalog"/> keys the same literal for its token set.
    /// </remarks>
    private const string DecompositionPromptFile = "create_subtasks.md";

    /// <summary>Command typed into the shell to start the CLI for a decomposition session.</summary>
    /// <remarks>
    /// The design fixes <c>yo</c> for the decomposition and subtask sessions. It coincides with
    /// phase 4's launcher but is not derived from it: a phase definition describes a phase, and
    /// changing the implementation phase's launcher must not silently re-aim decomposition.
    /// </remarks>
    private const string DecompositionLauncher = "yo";

    /// <summary>Prompt file that executes exactly one entry of the ordered subtask index.</summary>
    /// <remarks>
    /// Keyed by the same literal in <see cref="PromptTemplateCatalog"/>, which maps it to
    /// <see cref="PromptVariables.SubtaskRunNames"/>.
    /// </remarks>
    private const string SubtaskRunPromptFile = "run_subtask.md";

    /// <summary>Command typed into the shell to start the CLI for one subtask session.</summary>
    /// <remarks>
    /// Deliberately its own constant rather than an alias of <see cref="DecompositionLauncher"/>,
    /// for the same reason that one is not an alias of phase 4's launcher: decomposition and
    /// execution are separate sessions, and re-aiming one must never silently re-aim the other.
    /// </remarks>
    private const string SubtaskRunLauncher = "yo";

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

    // Identical to the ledger's own reader options on purpose: "the parse succeeded" must mean the
    // same thing here as it does there, or settling would either give up on a payload the ledger
    // accepts or stop on one it rejects.
    private static readonly JsonDocumentOptions StatusDocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private readonly IPromptTemplateService _prompts;
    /// <summary>Pause between the individual key presses of one auto-answer.</summary>
    private static readonly TimeSpan KeyPressGap = TimeSpan.FromMilliseconds(60);

    private readonly IAutoAnswerService _autoAnswer;
    private readonly IArtifactWatcherFactory _watchers;
    private readonly ITaskStateStore _state;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _pollInterval;
    private readonly Func<TimeSpan, CancellationToken, Task> _settleDelay;

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
    public WorkflowOrchestrator(
        IPromptTemplateService prompts,
        IAutoAnswerService autoAnswer,
        IArtifactWatcherFactory watchers,
        ITaskStateStore state,
        TimeSpan debounce,
        TimeSpan pollInterval,
        Func<TimeSpan, CancellationToken, Task>? settleDelay = null)
    {
        _prompts = prompts;
        _autoAnswer = autoAnswer;
        _watchers = watchers;
        _state = state;
        _debounce = debounce;
        _pollInterval = pollInterval;
        _settleDelay = settleDelay ?? Task.Delay;
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
        // a finished task would otherwise leave phases 2-4 marked Completed until the run reaches
        // them, so a crash during phase 1 recovers a tab with three phases painted green; and a
        // crash in the microsecond gap between RecordPhase(N, Completed) and
        // RecordPhase(N+1, Active) could let the next recovery skip N+1 (SPEC section 7.4, D24).
        ClearPhasesFrom(request.Paths, request.StartPhase);

        try
        {
            foreach (var definition in PhaseCatalog.All.Skip((int)request.StartPhase))
            {
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

        // Phase 4 splits here and nowhere else. Everything above is shared, so a disabled or
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

        // A marker left by a previous run of this task would satisfy the phase-4 watcher in
        // milliseconds. Deleting it before the baseline is taken removes the failure mode
        // instead of handling it (SPEC section 8.3). It stays in the phase method rather than
        // moving into RunSessionAsync because it is phase-specific; the baseline is the very
        // first statement of that method, so "before the baseline" still holds.
        if (definition.Phase == WorkflowPhase.Implementation)
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
            definition.Completion,
            request.Paths.TaskDirectory,
            WatchedPaths(request.Paths, definition),
            cancellationToken);

        _state.RecordPhase(request.Paths, definition.Phase, PhaseStatus.Completed);
        request.Progress.Report(new PhaseProgress(definition.Phase, PhaseStatus.Completed));
    }

    /// <summary>
    /// Phase 4 with subtask mode enabled, up to and including decomposition: re-validate the
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
        // Not a re-capture. The one capture was taken by the phase-4 guard in RunPhaseAsync a few
        // lines up - this run's entry into phase 4 - and Lazy<T> serves that same immutable record
        // here, so nothing in this method can observe the enabled flag at one moment and the
        // directory at another (requirement 1.6); SubtaskConfiguration.Capture, Deferred and
        // CaptureValidated take an ISubtaskConfiguration this method is never given. What phase-4
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

        request.SubtaskProgress?.Report(
            new SubtaskProgress(SubtaskStage.Decomposing, 0, 0, 0, null, []));

        // The reuse decision comes FIRST and the deletion only inside the branch it enables. A
        // readable ordered index is reused exactly as it stands (requirement 2.3), so deleting the
        // task-level result.json ahead of this test would destroy the very evidence recovery
        // depends on and re-decompose a task that is already half executed. What is deleted is
        // therefore only a *stale* flag: a file that exists, is non-empty enough to satisfy the
        // watcher's FilesExist rule on its first poll, and yet yields no order at all.
        if (!SubtaskLedger.IsDecomposed(tracking))
        {
            // The watcher would create this itself; doing it here states that composing the
            // tracking task folder is the application's job and lets the delete below run against
            // an existing directory.
            Directory.CreateDirectory(tracking.TaskDirectory);
            DeleteStaleFlag(tracking.ResultAbsolute);

            await RunDecompositionSessionAsync(request, tracking, cancellationToken);
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

        await RunSubtaskLoopAsync(request, tracking, cancellationToken);

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
    /// <param name="cancellationToken">Cancels the loop and the session it is waiting on.</param>
    /// <remarks>
    /// <para>
    /// The ledger is re-read at the top of every iteration rather than being materialised once: a
    /// session writes the very files the next decision reads, and requirement 4.5 forbids a stored
    /// current-subtask cursor. The only state this run keeps in memory is
    /// <c>attempted</c> - the titles it has already started work on - and it is deliberately local
    /// to this method, so it cannot outlive the run. That single set is what makes eligibility
    /// <c>state != Complete &amp;&amp; !attemptedThisRun</c> (design "Session and Loop", issue 2
    /// resolved): an entry left <see cref="SubtaskStatus.Failed"/> or
    /// <see cref="SubtaskStatus.Pending"/> by an earlier run is eligible again here and gets
    /// exactly one fresh attempt, and no entry is ever attempted twice within one run.
    /// </para>
    /// <para>
    /// Comparison is <see cref="StringComparer.OrdinalIgnoreCase"/> because that is precisely how
    /// the ledger de-duplicates the index (requirement 2.12): a set that compared ordinally would
    /// see two entries where the ledger reports one and could attempt the same folder twice.
    /// </para>
    /// <para>
    /// Termination: every iteration either returns or adds one title to <c>attempted</c>, and only
    /// unattempted titles are eligible, so the loop is bounded by the number of entries the index
    /// lists. There is no execution timeout anywhere in it (requirement 2.8, E8); the bound is on
    /// iterations, never on how long a live session may take.
    /// </para>
    /// </remarks>
    private async Task RunSubtaskLoopAsync(
        WorkflowRunRequest request,
        SubtaskPaths tracking,
        CancellationToken cancellationToken)
    {
        var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

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

            var entry = snapshot.States.FirstOrDefault(
                state => state.Status != SubtaskStatus.Complete && !attempted.Contains(state.Title));

            if (entry is null)
            {
                return;
            }

            // Marked before any work, so every exit below - a skip, a failure, an exception -
            // still costs this entry its one attempt for this run.
            attempted.Add(entry.Title);

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
                continue;
            }

            // Only now, and only for the entry actually being attempted: a per-subtask result.json
            // left by an earlier run is non-empty, so CompletionRule.FilesExist would be satisfied
            // on the watcher's first poll and the session would end before the agent had written
            // anything (the same failure mode the task-level flag has).
            DeleteStaleFlag(tracking.SubtaskResultFile(entry.Title));

            await RunSubtaskSessionAsync(request, tracking, entry.Title, description, cancellationToken);

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
            SubtaskRunLauncher,
            SubtaskRunPromptFile,
            PromptVariables.ForSubtaskRun(
                request.Paths, request.TaskDescription, tracking, title, description),
            CompletionRule.FilesExist,
            tracking.SubtaskDirectory(title),
            [tracking.SubtaskResultFile(title)],
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
                snapshot.States));
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
            DecompositionLauncher,
            DecompositionPromptFile,
            PromptVariables.ForSubtaskCreation(request.Paths, request.TaskDescription, tracking),
            CompletionRule.FilesExist,
            tracking.TaskDirectory,
            [tracking.ResultAbsolute],
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
    /// <param name="completion">Completion rule the watcher applies.</param>
    /// <param name="watchDirectory">
    /// Root the watcher observes. It is a parameter rather than something derived inside, because
    /// a session that is not a phase run watches a different root than the task directory;
    /// deriving it here would hard-code the phase case into the shared primitive.
    /// </param>
    /// <param name="watchedPaths">Artefact paths the completion rule applies to.</param>
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
        CompletionRule completion,
        string watchDirectory,
        IReadOnlyList<string> watchedPaths,
        CancellationToken cancellationToken)
    {
        // The baseline for AnyContentChanged must be captured before the CLI can touch anything.
        using var watcher = _watchers.Create(
            completion,
            watchDirectory,
            watchedPaths,
            _debounce,
            _pollInterval);

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
        terminal.Send($"{launcher}\r");

        await SettleAndAnswerAsync(terminal, cancellationToken);

        await SendPromptAsync(
            terminal,
            _prompts.Render(promptFile, substitutions),
            cancellationToken);

        // Every phase now has an artefact condition; the manual signal is the escape hatch for
        // all four, not a completion rule of its own.
        var watched = watcher.WaitAsync(cancellationToken);
        var manual = manualSignal.WaitAsync(cancellationToken);

        var winner = await Task.WhenAny(watched, manual);

        // This second await is the outer half of the original `await await Task.WhenAny(...)` and
        // must stay: it unwraps the winning task so a watcher failure surfaces as
        // ArtifactWatchException instead of being silently dropped. It is awaited BEFORE the
        // winner is reported, so a faulted winner throws and this method returns nothing at all.
        await winner;

        return ReferenceEquals(winner, manual);
    }

    // Keyed on the phase, not the completion rule: phases 1 and 3 share the FilesExist/
    // AnyContentChanged rules but phase 2 watches a different artefact.
    private static IReadOnlyList<string> WatchedPaths(TaskPaths paths, PhaseDefinition definition) =>
        definition.Phase switch
        {
            WorkflowPhase.Specification => [paths.SpecAbsolute, paths.PlanAbsolute],
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
    /// <remarks>
    /// A missing file is not an error, so the absent case needs no probe. Failing to delete is not
    /// an error either: the session then completes as soon as it starts, and the user still has a
    /// live terminal and the manual completion action.
    /// </remarks>
    private static void DeleteStaleFlag(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Held open by another process. The phase then completes immediately; the user can
            // still drive the session by hand and 'Task abschliessen' remains available.
        }
        catch (UnauthorizedAccessException)
        {
            // Read-only or no permission. Same fallback as above.
        }
    }

    // The prompt used to be submitted by writing the paste block and then a carriage return after
    // a fixed 150 ms. Claude Code's TUI coalesces a paste, so a CR arriving while a multi-kilobyte
    // prompt is still draining is taken as a literal newline instead of as submit - which is why
    // the user had to press Enter by hand in every phase. Waiting for the screen to go quiet and
    // then verifying that the CR produced output removes the dependency on any fixed delay.
    private async Task SendPromptAsync(
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

    private async Task SettleAndAnswerAsync(ITerminalController terminal, CancellationToken cancellationToken)
    {
        var configuration = _autoAnswer.RuleSet;
        var fired = new HashSet<string>(StringComparer.Ordinal);
        var quietPeriod = TimeSpan.FromMilliseconds(configuration.QuietPeriodMs);
        var overall = Stopwatch.StartNew();

        while (overall.Elapsed < TimeSpan.FromMilliseconds(configuration.SettleTimeoutMs))
        {
            cancellationToken.ThrowIfCancellationRequested();

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

            var rule = _autoAnswer.Match(screen, fired);

            if (rule is null || fired.Count >= configuration.MaxAnswersPerPhase)
            {
                // Nothing left to answer. But a quiet screen is NOT proof the launcher is ready:
                // right after `yo` is typed there is a quiet window while Node/Claude cold-starts
                // during which the screen still shows the shell prompt. Pasting the prompt then
                // sends it into the shell (or into Claude before its input is listening) and it is
                // lost, so the Enter that follows has nothing to submit. Only proceed once the
                // launcher's own input is on screen (spec section 7.3).
                if (_autoAnswer.IsLauncherReady(screen))
                {
                    return;
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

        // Ceiling reached: the launcher never rendered, or rules kept matching. The prompt is
        // sent anyway and the user can intervene in the live terminal.
    }
}
