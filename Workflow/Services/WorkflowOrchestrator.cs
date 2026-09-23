using System.Diagnostics;
using System.IO;
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

    private readonly IPromptTemplateService _prompts;
    /// <summary>Pause between the individual key presses of one auto-answer.</summary>
    private static readonly TimeSpan KeyPressGap = TimeSpan.FromMilliseconds(60);

    private readonly IAutoAnswerService _autoAnswer;
    private readonly IArtifactWatcherFactory _watchers;
    private readonly ITaskStateStore _state;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _pollInterval;

    /// <summary>Creates the orchestrator.</summary>
    /// <param name="prompts">Prompt renderer.</param>
    /// <param name="autoAnswer">Auto-answer rule engine.</param>
    /// <param name="watchers">Artefact watcher factory.</param>
    /// <param name="state">The per-task workflow journal.</param>
    /// <param name="debounce">Debounce applied to file-system notifications.</param>
    /// <param name="pollInterval">Fallback poll interval for dropped file-system events.</param>
    public WorkflowOrchestrator(
        IPromptTemplateService prompts,
        IAutoAnswerService autoAnswer,
        IArtifactWatcherFactory watchers,
        ITaskStateStore state,
        TimeSpan debounce,
        TimeSpan pollInterval)
    {
        _prompts = prompts;
        _autoAnswer = autoAnswer;
        _watchers = watchers;
        _state = state;
        _debounce = debounce;
        _pollInterval = pollInterval;
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
        if (definition.Phase == WorkflowPhase.Implementation
            && SubtaskConfiguration.IsEnabled(request.Subtasks))
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
        // Not a re-capture. The snapshot was taken once, above this boundary, and arrives as an
        // immutable record precisely so that nothing here can observe the enabled flag at one
        // moment and the directory at another (requirement 1.6); SubtaskConfiguration.Capture and
        // CaptureValidated take an ISubtaskConfiguration this method is never given. What phase-4
        // entry owes the run is the second validation - the marker can disappear between the start
        // gate and this moment - and EnsureUsable is that step, the same call CaptureValidated
        // makes after capturing (requirement 1.8).
        var configuration = request.Subtasks!;
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
    }

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
