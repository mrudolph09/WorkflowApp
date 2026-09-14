using System.Diagnostics;
using System.IO;
using System.Text;
using Workflow.Models;
using Workflow.Services;
using Workflow.Tests.Fakes;

namespace Workflow.Tests;

public sealed class WorkflowOrchestratorTests : IDisposable
{
    private readonly string _root;
    private readonly string _promptDir;
    private readonly string _rulesPath;
    private readonly TaskPaths _paths;
    private readonly List<PhaseProgress> _progress = [];

    public WorkflowOrchestratorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wf-orch-" + Guid.NewGuid().ToString("N"));
        _promptDir = Path.Combine(_root, "Prompt");
        Directory.CreateDirectory(_promptDir);

        foreach (var definition in PhaseCatalog.All)
        {
            File.WriteAllText(
                Path.Combine(_promptDir, definition.PromptFile),
                $"PROMPT {definition.Phase} {{taskbeschreibung}} {{spec_path}} {{plan_path}} {{review_path}}");
        }

        _rulesPath = Path.Combine(_root, "rules.json");
        File.WriteAllText(_rulesPath, """
        {
          "version": 1, "quietPeriodMs": 10, "settleTimeoutMs": 2000, "maxAnswersPerPhase": 3,
          "rules": [ { "id": "bypass", "pattern": "(?is)bypass", "send": "2\\r", "description": "" } ]
        }
        """);

        var workspace = Path.Combine(_root, "ws");
        Directory.CreateDirectory(workspace);
        _paths = new TaskPaths(workspace, "demo");
        Directory.CreateDirectory(_paths.TaskDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private WorkflowOrchestrator CreateOrchestrator() => new(
        new PromptTemplateService(_promptDir),
        new AutoAnswerService(_rulesPath, overridePath: null),
        new ArtifactWatcherFactory(),
        TimeSpan.FromMilliseconds(20),
        TimeSpan.FromMilliseconds(40));

    private WorkflowRunRequest CreateRequest(FakeTerminalController terminal, ManualPhaseSignal signal) =>
        new(_paths, "Beschreibung", terminal, signal, new Progress<PhaseProgress>(p =>
        {
            lock (_progress)
            {
                _progress.Add(p);
            }
        }));

    private void WriteArtifacts(params string[] paths)
    {
        foreach (var path in paths)
        {
            File.WriteAllText(path, "content-" + Guid.NewGuid().ToString("N"));
        }
    }

    [Fact]
    public async Task Phase1_WritesCdThenLauncherThenPastesThePrompt()
    {
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, signal), cts.Token);

        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);

        Assert.Equal($"cd \"{_paths.WorkingDirectory}\"\r", terminal.Sent[0]);
        Assert.Equal("yo\r", terminal.Sent[1]);
        Assert.Contains("PROMPT Specification", terminal.Pasted[0], StringComparison.Ordinal);
        Assert.Contains("Beschreibung", terminal.Pasted[0], StringComparison.Ordinal);
        Assert.Contains(_paths.SpecRelative, terminal.Pasted[0], StringComparison.Ordinal);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SettleLoop_SendsTheMatchingAutoAnswerBeforeThePrompt()
    {
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        terminal.QueueSnapshot("WARNING: Bypass Permissions mode\n 1. No, exit\n 2. Yes, I accept");
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, signal), cts.Token);

        // EmitOutput must happen AFTER the orchestrator's StartSession call, which resets
        // OutputCount/LastOutputUtc to a fresh baseline - calling it before RunAsync starts would
        // be silently wiped out and Gate A would never open.
        await Task.Delay(200, cts.Token);
        terminal.EmitOutput();

        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);

        Assert.Equal("2\r", terminal.Sent[2]);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    // --- Regressions pinned by the review -------------------------------------------------

    [Fact]
    public async Task ThePromptIsNotSentBeforeTheLauncherHasRendered()
    {
        // Production race: on a fresh session nothing has been drawn yet. A loop that judges
        // quietness alone would snapshot an empty buffer, match nothing, and paste the prompt
        // into the still-blocking confirmation dialog.
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, signal), cts.Token);

        // The launcher has produced nothing: no snapshot is queued and OutputCount stays 0.
        await Task.Delay(400, cts.Token);
        Assert.Empty(terminal.Pasted);

        // Now the launcher paints its bypass warning.
        terminal.QueueSnapshot("bypass permissions mode");
        terminal.EmitOutput();

        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);
        Assert.Contains("2\r", terminal.Sent);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task NothingIsWrittenToTheTerminalBeforeItReportsReady()
    {
        // The WebView2 page installs its message listener on `ready`. Anything written earlier
        // is silently dropped, including the startup screen the auto-answer rules need.
        var terminal = new FakeTerminalController();   // ReadyGate deliberately left uncompleted
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, signal), cts.Token);

        await Task.Delay(400, cts.Token);
        Assert.Empty(terminal.Sent);
        Assert.Empty(terminal.Pasted);

        terminal.ReadyGate.SetResult();
        terminal.QueueSnapshot("bypass permissions mode");
        terminal.EmitOutput();

        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task ANonMatchingRuleSetSendsThePromptPromptlyInsteadOfWaitingOutTheCeiling()
    {
        // Decision D13: exit-on-first-non-match is the NORMAL path. settleTimeoutMs is a ceiling,
        // not a mandatory wait - otherwise every phase of every task would cost an extra minute.
        // The fixture sets settleTimeoutMs to 2000 ms and quietPeriodMs to 10 ms.
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        terminal.QueueSnapshot("PS C:\\ws>");          // launcher is at its input prompt
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var started = Stopwatch.StartNew();
        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, signal), cts.Token);

        // EmitOutput must happen AFTER StartSession (called from inside RunAsync), which resets
        // OutputCount/LastOutputUtc - emitting before RunAsync starts is silently wiped out and
        // Gate A never opens, making the loop run out the full settleTimeoutMs ceiling instead.
        await Task.Delay(200, cts.Token);
        terminal.EmitOutput();

        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);
        started.Stop();

        Assert.DoesNotContain(terminal.Sent, s => s == "2\r");
        Assert.True(
            started.Elapsed < TimeSpan.FromSeconds(2),
            $"The prompt took {started.Elapsed} - the loop waited out settleTimeoutMs instead of exiting on the first quiet snapshot.");

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task TheSubmitCarriageReturnBelongsToTheSessionThatReceivedThePaste()
    {
        // A reused task folder can satisfy phase 1's watcher immediately (spec section 8.3), so
        // the orchestrator may start phase 2 within the paste's submit delay. The CR must not
        // land in the new launcher, where it would accept `yo`'s preselected "No, exit".
        WriteArtifacts(_paths.SpecAbsolute, _paths.PlanAbsolute);   // phase 1 completes at once

        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, signal), cts.Token);

        terminal.QueueSnapshot("PS>");
        terminal.EmitOutput();
        await WaitUntil(() => terminal.StartedSessions.Count >= 2, cts.Token);

        // Every paste is fully written - including its CR - before the next session starts.
        Assert.Equal(terminal.Pasted.Count, terminal.SubmitsCompletedBeforeNextSession);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }


    [Fact]
    public async Task AnAutoAnswerRuleFiresAtMostOncePerPhase()
    {
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        terminal.QueueSnapshot("bypass");
        terminal.QueueSnapshot("bypass");
        terminal.QueueSnapshot("bypass");
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, signal), cts.Token);

        // Must happen after StartSession's reset - see the identical note above.
        await Task.Delay(200, cts.Token);
        terminal.EmitOutput();

        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);

        Assert.Single(terminal.Sent, s => s == "2\r");

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Phases_ChainThroughAllFourStations()
    {
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, signal), cts.Token);

        // Phase 1 -> spec and plan appear.
        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);
        WriteArtifacts(_paths.SpecAbsolute, _paths.PlanAbsolute);

        // Phase 2 -> review appears.
        await WaitUntil(() => terminal.StartedSessions.Count >= 2, cts.Token);
        Assert.Equal("codex --yolo\r", terminal.Sent.Last(s => s.EndsWith('\r') && s.Contains("codex", StringComparison.Ordinal)));
        WriteArtifacts(_paths.ReviewAbsolute);

        // Phase 3 -> BOTH spec and plan content change (CompletionRule.AllContentChanged).
        await WaitUntil(() => terminal.StartedSessions.Count >= 3, cts.Token);
        await Task.Delay(80, cts.Token);
        WriteArtifacts(_paths.SpecAbsolute, _paths.PlanAbsolute);

        // Phase 4 -> waits for the manual signal.
        await WaitUntil(() => terminal.StartedSessions.Count >= 4, cts.Token);
        await Task.Delay(200, cts.Token);
        Assert.False(run.IsCompleted);

        signal.Signal();
        await run;

        Assert.Equal(4, terminal.StartedSessions.Count);
        Assert.Equal(4, terminal.Pasted.Count);

        lock (_progress)
        {
            Assert.Contains(_progress, p => p.Phase == WorkflowPhase.Specification && p.Status == PhaseStatus.Completed);
            Assert.Contains(_progress, p => p.Phase == WorkflowPhase.Review && p.Status == PhaseStatus.Completed);
            Assert.Contains(_progress, p => p.Phase == WorkflowPhase.ResolveReview && p.Status == PhaseStatus.Completed);
            Assert.Contains(_progress, p => p.Phase == WorkflowPhase.Implementation && p.Status == PhaseStatus.Completed);
        }
    }

    [Fact]
    public async Task ManualSignal_AlsoEndsANonManualPhase()
    {
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, signal), cts.Token);

        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);
        signal.Signal();

        await WaitUntil(() => terminal.StartedSessions.Count >= 2, cts.Token);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Cancellation_DisposesTheSession()
    {
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource();

        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, signal), cts.Token);

        await WaitUntil(() => terminal.Pasted.Count >= 1, CancellationToken.None);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.True(terminal.DisposeCount >= 1);
    }

    [Fact]
    public async Task RunAsync_DeletesAStaleDoneMarkerBeforePhaseFourWaits()
    {
        await File.WriteAllTextAsync(_paths.DoneAbsolute, "left over from a previous run");

        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, signal), cts.Token);

        // Satisfy phases 1-3 so the run reaches phase 4.
        await WaitForPhaseAsync(WorkflowPhase.Specification, cts.Token);
        WriteArtifacts(_paths.SpecAbsolute, _paths.PlanAbsolute);
        await WaitForPhaseAsync(WorkflowPhase.Review, cts.Token);
        WriteArtifacts(_paths.ReviewAbsolute);
        await WaitForPhaseAsync(WorkflowPhase.ResolveReview, cts.Token);
        WriteArtifacts(_paths.SpecAbsolute, _paths.PlanAbsolute);
        await WaitForPhaseAsync(WorkflowPhase.Implementation, cts.Token);

        // The stale marker must be gone, so phase 4 is still waiting.
        Assert.False(File.Exists(_paths.DoneAbsolute));
        Assert.DoesNotContain(
            _progress, p => p.Phase == WorkflowPhase.Implementation && p.Status == PhaseStatus.Completed);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    private Task WaitForPhaseAsync(WorkflowPhase phase, CancellationToken cancellationToken) =>
        WaitUntil(
            () =>
            {
                lock (_progress)
                {
                    return _progress.Any(p => p.Phase == phase && p.Status == PhaseStatus.Active);
                }
            },
            cancellationToken);

    private static async Task WaitUntil(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met in time.");
            }

            await Task.Delay(20, CancellationToken.None);
        }
    }
}
