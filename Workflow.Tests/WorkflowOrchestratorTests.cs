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
    private readonly FakeTaskStateStore _state = new();

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
          "version": 2, "quietPeriodMs": 10, "settleTimeoutMs": 2000, "maxAnswersPerPhase": 3,
          "pasteQuietPeriodMs": 10, "pasteSettleTimeoutMs": 300, "submitVerifyMs": 30, "maxSubmitAttempts": 2,
          "rules": [ { "id": "bypass", "pattern": "(?is)bypass", "send": "\\e[B\\r", "description": "" } ]
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
        _state,
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

        // The answer arrives as discrete key presses, the way a TUI's keypress parser expects
        // them: a single write of "<Down><Enter>" is parsed by Ink as one unknown key and ignored.
        Assert.Equal("\u001b[B", terminal.Sent[2]);
        Assert.Equal("\r", terminal.Sent[3]);

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
        Assert.Contains("\u001b[B", terminal.Sent);

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

        Assert.DoesNotContain(terminal.Sent, s => s == "\u001b[B");
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

        Assert.Single(terminal.Sent, s => s == "\u001b[B");

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

    [Fact]
    public async Task RunAsync_StartPhaseResolveReview_SkipsPhasesOneAndTwo()
    {
        var terminal = new FakeTerminalController();
        var signal = new ManualPhaseSignal();
        terminal.ReadyGate.TrySetResult();

        var request = CreateRequest(terminal, signal) with { StartPhase = WorkflowPhase.ResolveReview };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = CreateOrchestrator().RunAsync(request, cts.Token);

        await WaitForPhaseAsync(WorkflowPhase.ResolveReview, cts.Token);

        lock (_progress)
        {
            Assert.DoesNotContain(_progress, p => p.Phase == WorkflowPhase.Specification);
            Assert.DoesNotContain(_progress, p => p.Phase == WorkflowPhase.Review);
        }

        Assert.Single(terminal.StartedSessions);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task RunAsync_ClearsTheJournalTailFromTheStartPhaseBeforeRunning()
    {
        // A previously finished task, re-entered at phase 3.
        var finished = new TaskState { TaskDescription = "d", UpdatedUtc = DateTimeOffset.UtcNow };
        foreach (var definition in PhaseCatalog.All)
        {
            finished.Phases.Add(new TaskPhaseState(definition.Phase, PhaseStatus.Completed, DateTimeOffset.UtcNow));
        }

        _state.Seed(_paths, finished);

        var terminal = new FakeTerminalController();
        var signal = new ManualPhaseSignal();
        terminal.ReadyGate.TrySetResult();

        var request = CreateRequest(terminal, signal) with { StartPhase = WorkflowPhase.ResolveReview };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = CreateOrchestrator().RunAsync(request, cts.Token);

        await WaitForPhaseAsync(WorkflowPhase.ResolveReview, cts.Token);

        var journal = _state.TryLoad(_paths)!;

        // Phases before the start phase keep their record - they are the resumed run's only
        // evidence that they happened.
        Assert.Equal(PhaseStatus.Completed, journal.Phases[0].Status);
        Assert.Equal(PhaseStatus.Completed, journal.Phases[1].Status);

        // The tail was cleared before anything ran; phase 3 is Active because it is running now
        // and phase 4 must NOT still read Completed (SPEC 7.4, D24).
        Assert.Equal(PhaseStatus.Active, journal.Phases[2].Status);
        Assert.Equal(PhaseStatus.Pending, journal.Phases[3].Status);
        Assert.Null(journal.Phases[3].CompletedUtc);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task RunAsync_RecordsEveryExecutedPhaseInTheJournal()
    {
        var terminal = new FakeTerminalController();
        var signal = new ManualPhaseSignal();
        terminal.ReadyGate.TrySetResult();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, signal), cts.Token);

        await WaitForPhaseAsync(WorkflowPhase.Specification, cts.Token);
        WriteArtifacts(_paths.SpecAbsolute, _paths.PlanAbsolute);
        await WaitForPhaseAsync(WorkflowPhase.Review, cts.Token);

        Assert.Equal(
            new[]
            {
                (WorkflowPhase.Specification, PhaseStatus.Active),
                (WorkflowPhase.Specification, PhaseStatus.Completed),
                (WorkflowPhase.Review, PhaseStatus.Active),
            },
            _state.Recorded.Take(3));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task RunAsync_LauncherProducesNoOutputAfterTheCr_SendsASecondCr()
    {
        var terminal = new FakeTerminalController();
        var signal = new ManualPhaseSignal();
        terminal.ReadyGate.TrySetResult();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, signal), cts.Token);

        await WaitForPhaseAsync(WorkflowPhase.Specification, cts.Token);
        await WaitUntilAsync(() => terminal.Pasted.Count == 1 && terminal.Sent.Count(t => t == "\r") == 2);

        Assert.Equal(2, terminal.Sent.Count(t => t == "\r"));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task RunAsync_LauncherRespondsToTheCr_SendsOnlyOne()
    {
        var terminal = new FakeTerminalController();
        var signal = new ManualPhaseSignal();
        terminal.ReadyGate.TrySetResult();
        terminal.EmitOutputOnNextCarriageReturn = true;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, signal), cts.Token);

        await WaitForPhaseAsync(WorkflowPhase.Specification, cts.Token);
        await WaitUntilAsync(() => terminal.Pasted.Count == 1);
        await Task.Delay(200, cts.Token);

        Assert.Equal(1, terminal.Sent.Count(t => t == "\r"));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task RunAsync_PasteEchoArrivesAfterSendPasteReturns_DoesNotSubmitBeforeIt()
    {
        var terminal = new FakeTerminalController();
        var signal = new ManualPhaseSignal();
        terminal.ReadyGate.TrySetResult();

        // The PTY echoes the bracketed-paste block only 80 ms after SendPaste has returned, and
        // the launcher answers the CR. The quiet period is longer than the echo delay, so a
        // correct gate must still be waiting when that echo lands.
        terminal.PasteEchoDelay = TimeSpan.FromMilliseconds(80);
        terminal.EmitOutputOnNextCarriageReturn = true;

        var orchestrator = CreateOrchestratorWithRules("""
        {
          "version": 2, "quietPeriodMs": 10, "settleTimeoutMs": 300, "maxAnswersPerPhase": 3,
          "pasteQuietPeriodMs": 300, "pasteSettleTimeoutMs": 3000,
          "submitVerifyMs": 20, "maxSubmitAttempts": 2,
          "rules": []
        }
        """);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = orchestrator.RunAsync(CreateRequest(terminal, signal), cts.Token);

        await WaitUntilAsync(() => terminal.Sent.Contains("\r"));

        // The whole point: by the time the first carriage return was written, the paste echo had
        // already been seen. A gate reading LastOutputUtc alone writes it while OutputCount is
        // still 0, because SettleAndAnswerAsync only ever returns once that timestamp is already
        // stale past the paste quiet period (SPEC section 9.3, D20).
        var firstCr = terminal.Sent
            .Select((text, index) => (text, index))
            .First(entry => entry.text == "\r")
            .index;

        Assert.True(
            terminal.OutputCountAtSend[firstCr] >= 1,
            "The submitting CR was written before the paste echo arrived.");

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SettleLoop_DoesNotPasteUntilTheLauncherReadyMarkerAppears()
    {
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        terminal.EmitOutputOnNextCarriageReturn = true;
        terminal.CurrentSnapshot = "PS C:\\ws> yo";   // launcher typed, but Claude not up yet
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var json = """
        {
          "version": 2, "quietPeriodMs": 20, "settleTimeoutMs": 20000, "maxAnswersPerPhase": 5,
          "pasteQuietPeriodMs": 20, "pasteSettleTimeoutMs": 2000, "submitVerifyMs": 20, "maxSubmitAttempts": 2,
          "readyPattern": "(?is)shift\\+tab to cycle",
          "rules": []
        }
        """;

        var run = CreateOrchestratorWithRules(json).RunAsync(CreateRequest(terminal, signal), cts.Token);

        await Task.Delay(200, cts.Token);
        terminal.EmitOutput();  // Gate A opens; the screen is quiet but shows only the shell prompt

        // The prompt must NOT be pasted into a shell while Claude Code is still cold-starting.
        await Task.Delay(600, cts.Token);
        Assert.Empty(terminal.Pasted);

        // Once Claude Code's footer appears, the prompt is pasted.
        terminal.CurrentSnapshot = "\u23f5\u23f5 bypass permissions on (shift+tab to cycle)";
        terminal.EmitOutput();
        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SendPrompt_PressesEnterUntilTheCollapsedPasteChipDisappears()
    {
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        // Ready AND a pasted-but-unsubmitted prompt sitting in the input box.
        terminal.CurrentSnapshot = "esc to interrupt\n[Pasted text #1 +40 lines] paste again to expand";
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var json = """
        {
          "version": 2, "quietPeriodMs": 20, "settleTimeoutMs": 20000, "maxAnswersPerPhase": 5,
          "pasteQuietPeriodMs": 20, "pasteSettleTimeoutMs": 2000, "submitVerifyMs": 40, "maxSubmitAttempts": 10,
          "readyPattern": "(?is)esc to interrupt",
          "pastePendingPattern": "(?is)paste again to expand",
          "rules": []
        }
        """;

        var run = CreateOrchestratorWithRules(json).RunAsync(CreateRequest(terminal, signal), cts.Token);

        await Task.Delay(150, cts.Token);
        terminal.EmitOutput();  // Gate A opens; screen already shows the ready marker
        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);

        // The prompt is pasted but not yet submitted: one carriage return does not clear the chip.
        // After a few Enters the launcher accepts the submit, so we clear the chip.
        await WaitUntil(() => terminal.Sent.Count(t => t == "\r") >= 3, cts.Token);
        terminal.CurrentSnapshot = "esc to interrupt";  // chip gone -> submitted

        // The submit loop stops once the chip is gone: the carriage-return count settles.
        await Task.Delay(300, cts.Token);
        var settled = terminal.Sent.Count(t => t == "\r");
        await Task.Delay(300, cts.Token);
        Assert.Equal(settled, terminal.Sent.Count(t => t == "\r"));
        Assert.True(settled >= 3, $"Expected several submit attempts, saw {settled}.");
        Assert.True(settled < 10, "The submit loop should have stopped well before the ceiling.");

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    // --- Phase 4, subtask mode (task 4.2) -------------------------------------------------

    /// <summary>The decomposition prompt file name; the catalog keys it by this literal too.</summary>
    private const string DecompositionPromptFile = "create_subtasks.md";

    private readonly List<SubtaskProgress> _subtaskProgress = [];

    /// <summary>Stages a tracking repository, with or without the marker that identifies one.</summary>
    private string CreateTrackingRepository(bool withTemplateMarker)
    {
        var directory = Path.Combine(_root, "workflows");
        Directory.CreateDirectory(directory);

        if (withTemplateMarker)
        {
            Directory.CreateDirectory(Path.Combine(directory, SubtaskPaths.TemplateFolderName));
        }

        return directory;
    }

    // Every token the decomposition set supplies is present, so a variable builder that dropped one
    // would throw PromptTemplateException instead of quietly rendering a shorter prompt.
    private void WriteDecompositionPrompt() => File.WriteAllText(
        Path.Combine(_promptDir, DecompositionPromptFile),
        "ZERLEGUNG {tasktitel} | {workflow_path} | {task_path} | {subtask_path} | {spec_path} | {plan_path}");

    private WorkflowRunRequest CreateSubtaskRequest(
        FakeTerminalController terminal,
        ManualPhaseSignal signal,
        string trackingDirectory) =>
        CreateRequest(terminal, signal) with
        {
            StartPhase = WorkflowPhase.Implementation,
            Subtasks = new SubtaskConfiguration(true, trackingDirectory),
            SubtaskProgress = new Progress<SubtaskProgress>(report =>
            {
                lock (_subtaskProgress)
                {
                    _subtaskProgress.Add(report);
                }
            }),
        };

    private SubtaskProgress[] SubtaskReports()
    {
        lock (_subtaskProgress)
        {
            return [.. _subtaskProgress];
        }
    }

    private static void WriteIndex(SubtaskPaths tracking, params string[] titles)
    {
        Directory.CreateDirectory(tracking.TaskDirectory);

        var listed = string.Join(",", titles.Select(title => $"\"{title}\""));
        File.WriteAllText(tracking.ResultAbsolute, $$"""{"version":1,"task":"demo","subtasks":[{{listed}}]}""");
    }

    private static void StageSubtask(SubtaskPaths tracking, string title, string? status)
    {
        Directory.CreateDirectory(tracking.SubtaskDirectory(title));
        File.WriteAllText(tracking.SubtaskMarkdown(title), $"# {title} - vollstaendige Beschreibung.");

        if (status is not null)
        {
            File.WriteAllText(tracking.SubtaskStatusFile(title), $$"""{"status":"{{status}}"}""");
        }
    }

    [Fact]
    public async Task PhaseFour_SubtaskModeDisabled_KeepsTheDoneMarkerImplementationPath()
    {
        // Requirement 2.1: an explicitly disabled snapshot must be indistinguishable from the null
        // default - same prompt, same stale-marker deletion, same done-marker completion.
        await File.WriteAllTextAsync(_paths.DoneAbsolute, "left over from a previous run");

        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var request = CreateRequest(terminal, signal) with
        {
            StartPhase = WorkflowPhase.Implementation,
            Subtasks = SubtaskConfiguration.Disabled,
        };

        var run = CreateOrchestrator().RunAsync(request, cts.Token);

        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);

        Assert.False(File.Exists(_paths.DoneAbsolute));
        Assert.Contains("PROMPT Implementation", terminal.Pasted[0], StringComparison.Ordinal);

        WriteArtifacts(_paths.DoneAbsolute);
        await run;

        Assert.Single(terminal.StartedSessions);
        Assert.Contains(
            _state.Recorded, r => r.Phase == WorkflowPhase.Implementation && r.Status == PhaseStatus.Completed);
        Assert.Empty(_state.SubtaskSettings);
    }

    [Fact]
    public async Task PhaseFour_SubtaskModeWithAReadableIndex_StartsNoDecompositionSession()
    {
        // Requirement 2.3: a readable ordered index is reused as it stands. The fixture is
        // deliberately asymmetric - complete, failed and pending entries - so a branch that reuses
        // only a uniformly pending index cannot pass.
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();
        WriteSubtaskRunPrompt();

        WriteIndex(tracking, "ST-001-alpha", "ST-002-beta", "ST-003-gamma");
        StageSubtask(tracking, "ST-001-alpha", "complete");
        StageSubtask(tracking, "ST-002-beta", "failed");
        StageSubtask(tracking, "ST-003-gamma", status: null);

        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var run = CreateOrchestrator().RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory), cts.Token);

        // Task 4.3 turned "no session at all" into "no DECOMPOSITION session": a reusing run now
        // goes straight to executing the two entries that are not complete. The two prompts are
        // textually distinct, so the claim is pinned on the rendered text rather than on a session
        // count that execution legitimately raises.
        await WaitForPasteAsync(terminal, 0, cts.Token);
        PublishSubtaskResult(tracking, "ST-002-beta", "complete");
        await WaitForPasteAsync(terminal, 1, cts.Token);
        PublishSubtaskResult(tracking, "ST-003-gamma", "complete");

        await run;

        // Counters, not booleans: a branch that decomposed - once or twice - must not pass either.
        Assert.DoesNotContain(terminal.Pasted, text => text.Contains("ZERLEGUNG", StringComparison.Ordinal));
        Assert.Equal(2, terminal.StartedSessions.Count);
        Assert.Equal(2, terminal.Pasted.Count);

        // The reusable index is retained. This is also what fails if the stale-flag deletion is
        // moved ahead of the reuse check.
        Assert.True(File.Exists(tracking.ResultAbsolute), "The reusable index was deleted.");

        Assert.Contains(SubtaskReports(), r => r.Stage == SubtaskStage.Decomposing);

        Assert.Contains(
            _state.SubtaskSettings,
            s => s.Enabled && string.Equals(s.WorkflowDirectory, tracking.WorkflowDirectory, StringComparison.Ordinal));
        Assert.DoesNotContain(
            _state.Recorded, r => r.Phase == WorkflowPhase.Implementation && r.Status == PhaseStatus.Completed);
    }

    [Fact]
    public async Task PhaseFour_SubtaskModeWithoutAnIndex_StartsExactlyOneDecompositionSession()
    {
        // Requirement 2.2: a fresh decomposition session against the existing spec and plan, aimed
        // at the task-specific tracking destination.
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();

        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var run = CreateOrchestrator().RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory), cts.Token);

        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);

        // The session runs in the PRODUCT working directory - subtasks change product code - and
        // reaches the tracking repository only through the absolute tokens in the prompt text.
        Assert.Equal(_paths.WorkingDirectory, terminal.StartedSessions[0]);
        Assert.Equal($"cd \"{_paths.WorkingDirectory}\"\r", terminal.Sent[0]);
        Assert.Equal("yo\r", terminal.Sent[1]);
        Assert.Contains("ZERLEGUNG", terminal.Pasted[0], StringComparison.Ordinal);
        Assert.Contains(tracking.WorkflowDirectory, terminal.Pasted[0], StringComparison.Ordinal);
        Assert.Contains(tracking.TaskDirectory, terminal.Pasted[0], StringComparison.Ordinal);
        Assert.Contains(tracking.SubtaskPathToken, terminal.Pasted[0], StringComparison.Ordinal);
        Assert.Contains(_paths.SpecRelative, terminal.Pasted[0], StringComparison.Ordinal);

        // The completion flag is the task-level index in the tracking repository, never the
        // working directory's done marker.
        Assert.False(File.Exists(_paths.DoneAbsolute));
        await Task.Delay(200, cts.Token);
        Assert.False(run.IsCompleted);

        WriteIndex(tracking, "ST-001-alpha", "ST-002-beta");
        await run;

        Assert.Single(terminal.StartedSessions);
        Assert.Single(terminal.Pasted);
    }

    [Fact]
    public async Task PhaseFour_SubtaskModeWithAnUnusableFlag_DeletesItBeforeTheDecompositionBaseline()
    {
        // A non-empty but unreadable task-level result.json satisfies CompletionRule.FilesExist on
        // the first poll, so leaving it in place would end the decomposition session before the
        // agent had written anything.
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();

        Directory.CreateDirectory(tracking.TaskDirectory);
        await File.WriteAllTextAsync(tracking.ResultAbsolute, "kein JSON, aber nicht leer");

        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var run = CreateOrchestrator().RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory), cts.Token);

        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);

        Assert.False(
            File.Exists(tracking.ResultAbsolute),
            "The stale task-level flag survived into the decomposition session's baseline.");

        await Task.Delay(200, cts.Token);
        Assert.False(run.IsCompleted);

        WriteIndex(tracking, "ST-001-alpha");
        await run;

        Assert.Single(terminal.StartedSessions);
    }

    [Fact]
    public async Task PhaseFour_TrackingDirectoryLostItsMarker_SurfacesTheGermanErrorAndStaysRecoverable()
    {
        // Requirements 1.8 and 2.9: the marker can disappear between the start gate and phase-4
        // entry. The run raises, never falls back to a normal full-context implementation session.
        var tracking = CreateTrackingRepository(withTemplateMarker: false);
        WriteDecompositionPrompt();

        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var error = await Assert.ThrowsAsync<SubtaskConfigurationException>(
            () => CreateOrchestrator().RunAsync(CreateSubtaskRequest(terminal, signal, tracking), cts.Token));

        Assert.Contains(SubtaskPaths.TemplateFolderName, error.ValidationMessage, StringComparison.Ordinal);
        Assert.Contains(tracking, error.ValidationMessage, StringComparison.Ordinal);

        Assert.Empty(terminal.StartedSessions);
        Assert.Empty(_state.SubtaskSettings);

        // Recoverable: Implementation was recorded Active and never Completed.
        Assert.Contains(
            _state.Recorded, r => r.Phase == WorkflowPhase.Implementation && r.Status == PhaseStatus.Active);
        Assert.DoesNotContain(
            _state.Recorded, r => r.Phase == WorkflowPhase.Implementation && r.Status == PhaseStatus.Completed);
    }

    [Fact]
    public async Task PhaseFour_DecompositionPublishesAnUnusableIndex_SurfacesTheGermanErrorAndStaysRecoverable()
    {
        // Requirement 2.9: the flag arrived, so the session ends - but it carries no order, which
        // is the user-fixable error rather than a silently empty run.
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();

        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var run = CreateOrchestrator().RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory), cts.Token);

        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);

        // Non-empty, so the watcher is satisfied - but the subtasks array is empty, so no order
        // can be established.
        await File.WriteAllTextAsync(tracking.ResultAbsolute, """{"version":1,"subtasks":[]}""");

        var error = await Assert.ThrowsAsync<SubtaskConfigurationException>(() => run);

        Assert.Contains(tracking.ResultAbsolute, error.ValidationMessage, StringComparison.Ordinal);
        Assert.Contains("subtasks", error.ValidationMessage, StringComparison.Ordinal);

        Assert.Single(terminal.StartedSessions);
        Assert.DoesNotContain(
            _state.Recorded, r => r.Phase == WorkflowPhase.Implementation && r.Status == PhaseStatus.Completed);
    }

    // --- Phase 4, ordered subtask execution (task 4.3) ------------------------------------

    /// <summary>The subtask execution prompt file name; the catalog keys it by this literal too.</summary>
    private const string SubtaskRunPromptFile = "run_subtask.md";

    // Every token the run set supplies that the loop must resolve is present, so a session that
    // composed a path itself - or dropped the body - renders visibly differently.
    private void WriteSubtaskRunPrompt() => File.WriteAllText(
        Path.Combine(_promptDir, SubtaskRunPromptFile),
        "AUSFUEHRUNG <{subtask_title}> | {task_path} | {subtask_path} | {spec_path}\n{subtask}");

    /// <summary>Writes only the description of a subtask, leaving it without any status payload.</summary>
    private static void WriteDescription(SubtaskPaths tracking, string title, string body)
    {
        Directory.CreateDirectory(tracking.SubtaskDirectory(title));
        File.WriteAllText(tracking.SubtaskMarkdown(title), body);
    }

    /// <summary>Writes a status payload for one subtask, as an earlier run would have left it.</summary>
    private static void WriteStatus(SubtaskPaths tracking, string title, string json) =>
        File.WriteAllText(tracking.SubtaskStatusFile(title), json);

    /// <summary>Leaves a non-empty completion flag behind, as an earlier run would have.</summary>
    private static void WriteStaleFlag(SubtaskPaths tracking, string title) =>
        File.WriteAllText(tracking.SubtaskResultFile(title), "uebrig aus einem alten Lauf");

    /// <summary>Publishes one subtask's completion flag and, optionally, its status payload.</summary>
    private static void PublishSubtaskResult(SubtaskPaths tracking, string title, string? status)
    {
        if (status is not null)
        {
            File.WriteAllText(tracking.SubtaskStatusFile(title), $$"""{"status":"{{status}}"}""");
        }

        File.WriteAllText(tracking.SubtaskResultFile(title), $$"""{"subtask":"{{title}}"}""");
    }

    /// <summary>Waits for the session numbered <paramref name="index"/> to have pasted its prompt.</summary>
    private static Task WaitForPasteAsync(
        FakeTerminalController terminal, int index, CancellationToken cancellationToken) =>
        WaitUntil(() => terminal.Pasted.Count > index, cancellationToken);

    [Fact]
    public async Task PhaseFour_SubtaskLoop_RunsOnlyTheIncompleteEntriesInIndexOrder()
    {
        // Requirement 2.4 and the task's observable: three entries, the MIDDLE one already
        // complete, so exactly two sessions run and they run in index order. Every dimension the
        // loop evaluates is asymmetric: three distinct titles, three distinct descriptions, and
        // three different on-disk states (complete / failed / untouched).
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();
        WriteSubtaskRunPrompt();

        WriteIndex(tracking, "ST-001-alpha", "ST-002-beta", "ST-003-gamma");
        WriteDescription(tracking, "ST-001-alpha", "Beschreibung ALPHA-ONLY");
        WriteDescription(tracking, "ST-002-beta", "Beschreibung BETA-ONLY");
        WriteDescription(tracking, "ST-003-gamma", "Beschreibung GAMMA-ONLY");

        // The middle entry is already complete; the first is a leftover failure from an earlier
        // run, which requirement 2.7 makes eligible for exactly one fresh attempt.
        WriteStatus(tracking, "ST-002-beta", """{"status":"complete"}""");
        WriteStatus(tracking, "ST-001-alpha", """{"status":"failed","failreason":"vorheriger Lauf"}""");

        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var run = CreateOrchestrator().RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory), cts.Token);

        await WaitForPasteAsync(terminal, 0, cts.Token);

        // Session 1 is the FIRST index entry, not the first pending one and not the last.
        Assert.Contains("AUSFUEHRUNG <ST-001-alpha>", terminal.Pasted[0], StringComparison.Ordinal);
        Assert.Contains("Beschreibung ALPHA-ONLY", terminal.Pasted[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Beschreibung GAMMA-ONLY", terminal.Pasted[0], StringComparison.Ordinal);
        PublishSubtaskResult(tracking, "ST-001-alpha", "complete");

        await WaitForPasteAsync(terminal, 1, cts.Token);

        // Session 2 skips the complete middle entry and takes the third.
        Assert.Contains("AUSFUEHRUNG <ST-003-gamma>", terminal.Pasted[1], StringComparison.Ordinal);
        Assert.Contains("Beschreibung GAMMA-ONLY", terminal.Pasted[1], StringComparison.Ordinal);
        PublishSubtaskResult(tracking, "ST-003-gamma", "complete");

        await run;

        // A count, not a "contains": a loop that also ran the complete entry passes every
        // assertion above and fails only here.
        Assert.Equal(2, terminal.StartedSessions.Count);
        Assert.Equal(2, terminal.Pasted.Count);
        Assert.DoesNotContain(terminal.Pasted, text => text.Contains("ST-002-beta", StringComparison.Ordinal));

        // The complete entry's own files were never touched.
        Assert.False(File.Exists(tracking.SubtaskResultFile("ST-002-beta")));
    }

    [Fact]
    public async Task PhaseFour_SubtaskLoop_RunsTheSessionInTheProductDirectoryWithResolvedPaths()
    {
        // Requirement 2.5: the full description - not a path to it - and correctly resolved
        // tracking paths. The body is multi-line so a renderer that took only its first line, or
        // trimmed it, is visible.
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();
        WriteSubtaskRunPrompt();

        const string Body = "# ST-001-alpha\n\nSchritt eins.\nSchritt zwei mit Umlaut: Grösse.\n";

        WriteIndex(tracking, "ST-001-alpha");
        WriteDescription(tracking, "ST-001-alpha", Body);

        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var run = CreateOrchestrator().RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory), cts.Token);

        await WaitForPasteAsync(terminal, 0, cts.Token);

        Assert.Equal(_paths.WorkingDirectory, terminal.StartedSessions[0]);
        Assert.Equal($"cd \"{_paths.WorkingDirectory}\"\r", terminal.Sent[0]);
        Assert.Equal("yo\r", terminal.Sent[1]);

        Assert.Contains(Body, terminal.Pasted[0], StringComparison.Ordinal);
        Assert.Contains(tracking.TaskDirectory, terminal.Pasted[0], StringComparison.Ordinal);
        Assert.Contains(tracking.SubtaskPathToken, terminal.Pasted[0], StringComparison.Ordinal);
        Assert.Contains(_paths.SpecRelative, terminal.Pasted[0], StringComparison.Ordinal);

        // Requirement 2.8: no execution timeout. The flag has not been published, so the session
        // is still alive well past every settle ceiling the fixture configures.
        await Task.Delay(400, cts.Token);
        Assert.False(run.IsCompleted);

        PublishSubtaskResult(tracking, "ST-001-alpha", "complete");
        await run;

        Assert.Single(terminal.StartedSessions);
    }

    [Fact]
    public async Task PhaseFour_SubtaskLoop_AttemptsAnAlreadyFailedEntryExactlyOncePerRun()
    {
        // The task's second observable, and requirement 2.7: a run over an index whose only entry
        // already failed starts one session; the second pass within that same run starts none,
        // even though the entry is STILL failed when the loop looks again.
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();
        WriteSubtaskRunPrompt();

        WriteIndex(tracking, "ST-001-alpha");
        WriteDescription(tracking, "ST-001-alpha", "Beschreibung ALPHA-ONLY");
        WriteStatus(tracking, "ST-001-alpha", """{"status":"failed","failreason":"vorheriger Lauf"}""");

        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var run = CreateOrchestrator().RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory), cts.Token);

        await WaitForPasteAsync(terminal, 0, cts.Token);

        // The attempt ends with the entry still failed - exactly the state that made it eligible.
        PublishSubtaskResult(tracking, "ST-001-alpha", "failed");

        await run;

        Assert.Single(terminal.StartedSessions);
        Assert.Single(terminal.Pasted);
    }

    [Fact]
    public async Task PhaseFour_SubtaskLoop_SkipsUnsafeTitlesAndMissingDescriptions()
    {
        // Requirement 2.10: an unsafe title and an entry without a usable description each fail
        // only themselves, without a session and without composing a path outside the tracking
        // repository. The fixture is asymmetric: an unsafe title, a safe title whose subtask.md is
        // absent, a safe title whose subtask.md is blank, and one attemptable entry - last, so a
        // loop that stopped at the first bad entry never reaches it.
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();
        WriteSubtaskRunPrompt();

        WriteIndex(tracking, "..", "ST-002-beta", "ST-003-gamma", "ST-004-delta");
        Directory.CreateDirectory(tracking.SubtaskDirectory("ST-002-beta"));    // folder, no subtask.md
        WriteDescription(tracking, "ST-003-gamma", "   \n  ");                  // present but blank
        WriteDescription(tracking, "ST-004-delta", "Beschreibung DELTA-ONLY");

        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var run = CreateOrchestrator().RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory), cts.Token);

        await WaitForPasteAsync(terminal, 0, cts.Token);

        Assert.Contains("AUSFUEHRUNG <ST-004-delta>", terminal.Pasted[0], StringComparison.Ordinal);
        PublishSubtaskResult(tracking, "ST-004-delta", "complete");

        await run;

        Assert.Single(terminal.StartedSessions);

        // '..' resolves to the tracking TASK folder, so an entry that composed a path from it
        // would have deleted the ordered index itself before starting a session.
        Assert.True(
            File.Exists(tracking.ResultAbsolute),
            "The unsafe title '..' was taken to the filesystem and deleted the ordered index.");
    }

    [Fact]
    public async Task PhaseFour_SubtaskLoop_SkipsATitleThatCollapsesOntoAnotherEntrysFolder()
    {
        // Implementation note from task 1.1: requirement 2.10 rejects only the exact dot-names, so
        // 'ST-001-alpha.' is a valid index title - and Windows strips the trailing dot, so every
        // path composed from it is 'ST-001-alpha''s. Attempting it would delete that sibling's
        // freshly published flag, paste the sibling's description a second time, and point the
        // agent at the sibling's status file.
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();
        WriteSubtaskRunPrompt();

        WriteIndex(tracking, "ST-001-alpha", "ST-001-alpha.");
        WriteDescription(tracking, "ST-001-alpha", "Beschreibung ALPHA-ONLY");

        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var run = CreateOrchestrator().RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory), cts.Token);

        await WaitForPasteAsync(terminal, 0, cts.Token);

        Assert.Contains("AUSFUEHRUNG <ST-001-alpha>", terminal.Pasted[0], StringComparison.Ordinal);

        // Deliberately WITHOUT a status payload: the sibling therefore stays unfinished, so the
        // collapsing entry is still eligible on the next pass and the guard is the only thing that
        // can stop it. A 'complete' payload here would make the ledger report the collapsing entry
        // complete too - through the very collapse under test - and the fixture would prove nothing.
        PublishSubtaskResult(tracking, "ST-001-alpha", status: null);

        // Bounded and generous: the first session ends as soon as that flag lands, and an
        // unguarded loop deletes the flag and starts its second session before the watcher's next
        // poll - long before this elapses.
        await Task.Delay(1500, cts.Token);

        // The sibling's evidence survived the collapsing entry untouched.
        Assert.True(
            File.Exists(tracking.SubtaskResultFile("ST-001-alpha")),
            "The collapsing entry deleted its sibling's completion flag.");

        await run;

        Assert.Single(terminal.StartedSessions);
        Assert.Single(terminal.Pasted);
    }

    [Fact]
    public async Task PhaseFour_SubtaskLoop_DeletesThatEntrysStaleFlagBeforeItsSessionWaits()
    {
        // A per-subtask result.json left by an earlier run is non-empty, so it satisfies
        // CompletionRule.FilesExist on the watcher's first poll. Only the entry being attempted is
        // cleared - the sibling's stale flag stays, because it is that entry's evidence until its
        // own attempt begins.
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();
        WriteSubtaskRunPrompt();

        WriteIndex(tracking, "ST-001-alpha", "ST-002-beta");
        WriteDescription(tracking, "ST-001-alpha", "Beschreibung ALPHA-ONLY");
        WriteDescription(tracking, "ST-002-beta", "Beschreibung BETA-ONLY");
        WriteStaleFlag(tracking, "ST-001-alpha");
        WriteStaleFlag(tracking, "ST-002-beta");

        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var run = CreateOrchestrator().RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory), cts.Token);

        await WaitForPasteAsync(terminal, 0, cts.Token);

        Assert.False(
            File.Exists(tracking.SubtaskResultFile("ST-001-alpha")),
            "The attempted entry's stale flag survived into its own session's baseline.");
        Assert.True(
            File.Exists(tracking.SubtaskResultFile("ST-002-beta")),
            "A later entry's flag was deleted before that entry was attempted.");

        await Task.Delay(400, cts.Token);
        Assert.False(run.IsCompleted);

        PublishSubtaskResult(tracking, "ST-001-alpha", "complete");
        await WaitForPasteAsync(terminal, 1, cts.Token);

        Assert.Contains("AUSFUEHRUNG <ST-002-beta>", terminal.Pasted[1], StringComparison.Ordinal);
        Assert.False(File.Exists(tracking.SubtaskResultFile("ST-002-beta")));

        PublishSubtaskResult(tracking, "ST-002-beta", "complete");
        await run;

        Assert.Equal(2, terminal.StartedSessions.Count);
    }

    [Fact]
    public async Task PhaseFour_SubtaskLoop_ManualCompletionStopsTheRunInsteadOfBurningTheIndex()
    {
        // 'Task abschliessen' is an explicit override (design issue 6b). The still-armed signal
        // would end every following session the instant it started, so a loop that ignored it
        // would start a session per remaining entry.
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();
        WriteSubtaskRunPrompt();

        WriteIndex(tracking, "ST-001-alpha", "ST-002-beta", "ST-003-gamma");
        WriteDescription(tracking, "ST-001-alpha", "Beschreibung ALPHA-ONLY");
        WriteDescription(tracking, "ST-002-beta", "Beschreibung BETA-ONLY");
        WriteDescription(tracking, "ST-003-gamma", "Beschreibung GAMMA-ONLY");

        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var run = CreateOrchestrator().RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory), cts.Token);

        await WaitForPasteAsync(terminal, 0, cts.Token);

        signal.Signal();
        await run;

        Assert.Single(terminal.StartedSessions);
        Assert.Single(terminal.Pasted);

        // Recoverable: the two untouched entries were never attempted and Implementation is still
        // Active, so pressing Continue starts a new run over them.
        Assert.False(File.Exists(tracking.SubtaskResultFile("ST-002-beta")));
        Assert.DoesNotContain(
            _state.Recorded, r => r.Phase == WorkflowPhase.Implementation && r.Status == PhaseStatus.Completed);
    }

    // The fixture's single rules file is shared by every test in the class; this one needs its
    // own timings, so it gets its own file.
    private WorkflowOrchestrator CreateOrchestratorWithRules(string json)
    {
        var path = Path.Combine(_root, "rules-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);

        return new WorkflowOrchestrator(
            new PromptTemplateService(_promptDir),
            new AutoAnswerService(path, overridePath: null),
            new ArtifactWatcherFactory(),
            _state,
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(40));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("Condition was never met.");
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
