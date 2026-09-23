using System.Diagnostics;
using System.IO;
using Workflow.Models;
using Workflow.Terminal;

namespace Workflow.Services;

/// <inheritdoc cref="IWorkflowOrchestrator" />
public sealed class WorkflowOrchestrator : IWorkflowOrchestrator
{
    private const int SnapshotLines = 60;

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

        // A marker left by a previous run of this task would satisfy the phase-4 watcher in
        // milliseconds. Deleting it before the baseline is taken removes the failure mode
        // instead of handling it (SPEC section 8.3). It stays in the phase method rather than
        // moving into RunSessionAsync because it is phase-specific; the baseline is the very
        // first statement of that method, so "before the baseline" still holds.
        if (definition.Phase == WorkflowPhase.Implementation)
        {
            DeleteStaleDoneMarker(request.Paths.DoneAbsolute);
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

    private static void DeleteStaleDoneMarker(string path)
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
