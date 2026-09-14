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
    private readonly IAutoAnswerService _autoAnswer;
    private readonly IArtifactWatcherFactory _watchers;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _pollInterval;

    /// <summary>Creates the orchestrator.</summary>
    /// <param name="prompts">Prompt renderer.</param>
    /// <param name="autoAnswer">Auto-answer rule engine.</param>
    /// <param name="watchers">Artefact watcher factory.</param>
    /// <param name="debounce">Debounce applied to file-system notifications.</param>
    /// <param name="pollInterval">Fallback poll interval for dropped file-system events.</param>
    public WorkflowOrchestrator(
        IPromptTemplateService prompts,
        IAutoAnswerService autoAnswer,
        IArtifactWatcherFactory watchers,
        TimeSpan debounce,
        TimeSpan pollInterval)
    {
        _prompts = prompts;
        _autoAnswer = autoAnswer;
        _watchers = watchers;
        _debounce = debounce;
        _pollInterval = pollInterval;
    }

    /// <inheritdoc />
    public async Task RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            foreach (var definition in PhaseCatalog.All)
            {
                await RunPhaseAsync(request, definition, cancellationToken);
            }
        }
        finally
        {
            request.Terminal.DisposeSession();
        }
    }

    private async Task RunPhaseAsync(
        WorkflowRunRequest request,
        PhaseDefinition definition,
        CancellationToken cancellationToken)
    {
        request.Progress.Report(new PhaseProgress(definition.Phase, PhaseStatus.Active));
        request.ManualSignal.Reset();

        // A marker left by a previous run of this task would satisfy the phase-4 watcher in
        // milliseconds. Deleting it before the baseline is taken removes the failure mode
        // instead of handling it (SPEC section 8.3).
        if (definition.Phase == WorkflowPhase.Implementation)
        {
            DeleteStaleDoneMarker(request.Paths.DoneAbsolute);
        }

        // The baseline for AnyContentChanged must be captured before the CLI can touch anything.
        using var watcher = _watchers.Create(
            definition.Completion,
            request.Paths.TaskDirectory,
            WatchedPaths(request.Paths, definition),
            _debounce,
            _pollInterval);

        request.Terminal.ClearScreen();
        request.Terminal.StartSession(
            ShellLocator.FindShellExecutable(),
            ShellLocator.ShellArguments,
            request.Paths.WorkingDirectory);

        // The page must be listening before anything is written to it, or `clear`, the first
        // PTY bytes and the first snapshot are all dropped - and the launcher's startup screen
        // is exactly what the auto-answer rules need to match (spec section 6.4).
        await request.Terminal.WaitUntilReadyAsync(cancellationToken);

        // Always quoted: working directories contain spaces and umlauts.
        request.Terminal.Send($"cd \"{request.Paths.WorkingDirectory}\"\r");
        request.Terminal.Send($"{definition.Launcher}\r");

        await SettleAndAnswerAsync(request.Terminal, cancellationToken);

        var variables = PromptVariables.For(request.Paths, request.TaskDescription);
        // Awaited, including the submitting CR, and scoped to THIS session: a detached delay
        // can deliver the CR into the next phase's launcher whenever a reused task folder
        // already satisfies a watcher (spec sections 6.5 and 8.3).
        await request.Terminal.SendPasteAsync(
            _prompts.Render(definition.PromptFile, variables),
            cancellationToken);

        // Every phase now has an artefact condition; the manual signal is the escape hatch for
        // all four, not a completion rule of its own. The doubled await unwraps Task<Task> so a
        // watcher failure surfaces as ArtifactWatchException instead of being silently dropped.
        await await Task.WhenAny(
            watcher.WaitAsync(cancellationToken),
            request.ManualSignal.WaitAsync(cancellationToken));

        request.Progress.Report(new PhaseProgress(definition.Phase, PhaseStatus.Completed));
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

            // Settled with nothing left to answer: send the prompt NOW. Waiting out
            // SettleTimeoutMs here would add a minute to every phase of every task - this is the
            // normal exit from the loop, not the exceptional one (spec section 7.3, D13).
            if (rule is null || fired.Count >= configuration.MaxAnswersPerPhase)
            {
                return;
            }

            terminal.Send(rule.Send);
            fired.Add(rule.Id);

            await Task.Delay(quietPeriod, cancellationToken);
        }

        // Ceiling reached: the launcher never rendered, or rules kept matching. The prompt is
        // sent anyway and the user can intervene in the live terminal.
    }
}
