using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    // The decision pre-flight (B7) is switched off here: it is not the subject of these tests, and
    // it would put a decisions session and an approval hold in front of every subtask run. Its own
    // tests further down build their orchestrator with CreateUnattendedOrchestrator.
    private WorkflowOrchestrator CreateOrchestrator(
        Func<TimeSpan, CancellationToken, Task>? settleDelay = null) => new(
        new PromptTemplateService(_promptDir),
        new AutoAnswerService(_rulesPath, overridePath: null),
        new ArtifactWatcherFactory(),
        _state,
        TimeSpan.FromMilliseconds(20),
        TimeSpan.FromMilliseconds(40),
        settleDelay,
        requireDecisionApproval: false);

    /// <summary>
    /// An <see cref="IProgress{T}"/> that records on the thread that reports, in report order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not <see cref="Progress{T}"/>. That one captures the ambient synchronisation
    /// context and <em>posts</em> each report, so the orchestrator's report and the test's own
    /// resumption after <c>await run</c> become two independently dispatched work items. They can
    /// then be delivered out of order, and a report can still be in flight when the run it belongs
    /// to has already been awaited - which no test here has ever meant to assert about, and which
    /// several of them lose a coin toss over.
    /// </para>
    /// <para>
    /// Recording inline removes the dispatch instead of waiting it out, so "the run has ended"
    /// implies "everything it reported has been recorded". The production path is unaffected: the
    /// app supplies its own <see cref="Progress{T}"/>, whose marshalling to the UI thread is its
    /// business and is what <see cref="IProgress{T}"/> exists to abstract over.
    /// </para>
    /// </remarks>
    private sealed class InlineProgress<T>(Action<T> record) : IProgress<T>
    {
        public void Report(T value) => record(value);
    }

    // --- Launcher-evidence gate -------------------------------------------------------------
    //
    // The prompt may be pasted only on positive proof that a launcher is running. These tests pin
    // each branch of that evidence, because the failure they guard against is not a stalled phase
    // but a multi-kilobyte markdown prompt being executed by PowerShell one line at a time.

    private const string EvidenceRules = """
    {
      "version": 2, "quietPeriodMs": 20, "settleTimeoutMs": 600, "maxAnswersPerPhase": 5,
      "pasteQuietPeriodMs": 20, "pasteSettleTimeoutMs": 2000, "submitVerifyMs": 20, "maxSubmitAttempts": 2,
      "readyPattern": "(?is)esc to interrupt",
      "rules": []
    }
    """;

    [Fact]
    public async Task SettleLoop_ReturnMarkerObserved_DoesNotPaste()
    {
        // Arrange
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        terminal.CurrentSnapshot = "PS C:\\ws>";
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var run = CreateOrchestratorWithRules(EvidenceRules).RunAsync(CreateRequest(terminal, signal), cts.Token);

        // Act - the launcher starts (its run marker is latched), then exits back to the shell.
        // The two steps are separated on purpose: with the exit simulated before the loop has
        // ever seen a run marker, this would prove only the "no run marker" branch.
        await WaitUntil(() => terminal.LauncherNonce is not null, cts.Token);
        terminal.EmitOutput();
        await Task.Delay(250, cts.Token);
        terminal.SimulateLauncherExit();

        // Assert - well past the 600 ms ceiling, and still nothing written into the shell.
        await Task.Delay(1500, cts.Token);
        Assert.Empty(terminal.Pasted);
        Assert.DoesNotContain(terminal.Sent, s => s == "\r");

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SettleLoop_NoRunMarkerAtCeiling_DoesNotPaste()
    {
        // Arrange - a shell that never executed the line we typed into it.
        var terminal = new FakeTerminalController { SuppressMarkers = true };
        terminal.ReadyGate.SetResult();
        terminal.CurrentSnapshot = "PS C:\\ws>";
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var run = CreateOrchestratorWithRules(EvidenceRules).RunAsync(CreateRequest(terminal, signal), cts.Token);

        // Act
        await WaitUntil(() => terminal.LauncherNonce is not null, cts.Token);
        terminal.EmitOutput();

        // Assert - absence of evidence is never taken as evidence of a running launcher.
        await Task.Delay(1500, cts.Token);
        Assert.Empty(terminal.Pasted);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SettleLoop_RunMarkerWithoutReadyAtCeiling_Pastes()
    {
        // Arrange - a launcher is provably running; only its TUI is unrecognised.
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        terminal.CurrentSnapshot = "a TUI this build has never seen before";
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var run = CreateOrchestratorWithRules(EvidenceRules).RunAsync(CreateRequest(terminal, signal), cts.Token);

        // Act
        await WaitUntil(() => terminal.LauncherNonce is not null, cts.Token);
        terminal.EmitOutput();

        // Assert - a cosmetic wording change in a CLI we do not control must not stall every run.
        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SettleLoop_LauncherAppearsAfterTheStall_PastesExactlyOnce()
    {
        // Arrange
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        terminal.CurrentSnapshot = "PS C:\\ws>";
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        var run = CreateOrchestratorWithRules(EvidenceRules).RunAsync(CreateRequest(terminal, signal), cts.Token);

        await WaitUntil(() => terminal.LauncherNonce is not null, cts.Token);
        terminal.SimulateLauncherExit();
        await Task.Delay(1200, cts.Token);
        Assert.Empty(terminal.Pasted);

        // Act - the user starts a launcher by hand; its TUI clears the shell's marker away.
        terminal.CurrentSnapshot = "esc to interrupt";
        terminal.SimulateLauncherRelaunch();
        terminal.EmitOutput();

        // Assert
        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);
        await Task.Delay(400, cts.Token);
        Assert.Single(terminal.Pasted);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SettleLoop_ReadyMarkerWhileOutputContinues_DoesNotPasteBeforeTheQuietPeriod()
    {
        // Arrange - the same quiet gate the main loop applies must also hold after a stall, or a
        // footer painted mid-start would release the wait while the composer is not yet listening.
        const string slowQuietRules = """
        {
          "version": 2, "quietPeriodMs": 700, "settleTimeoutMs": 400, "maxAnswersPerPhase": 5,
          "pasteQuietPeriodMs": 20, "pasteSettleTimeoutMs": 2000, "submitVerifyMs": 20, "maxSubmitAttempts": 2,
          "readyPattern": "(?is)esc to interrupt",
          "rules": []
        }
        """;

        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        terminal.CurrentSnapshot = "PS C:\\ws>";
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var run = CreateOrchestratorWithRules(slowQuietRules).RunAsync(CreateRequest(terminal, signal), cts.Token);

        await WaitUntil(() => terminal.LauncherNonce is not null, cts.Token);
        terminal.SimulateLauncherExit();
        await Task.Delay(900, cts.Token);
        Assert.Empty(terminal.Pasted);

        // Act - ready on screen, but output keeps arriving so the screen is never quiet.
        terminal.CurrentSnapshot = "esc to interrupt";
        terminal.SimulateLauncherRelaunch();

        for (var i = 0; i < 12; i++)
        {
            terminal.EmitOutput();
            await Task.Delay(100, cts.Token);
        }

        // Assert
        Assert.Empty(terminal.Pasted);

        // And once the screen finally goes quiet for the full period, the prompt is sent.
        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SettleLoop_ManualSignalBeforeCeiling_DoesNotPaste()
    {
        // Arrange - a long ceiling, so a run that reacted only at the ceiling would be caught out.
        const string longCeilingRules = """
        {
          "version": 2, "quietPeriodMs": 20, "settleTimeoutMs": 30000, "maxAnswersPerPhase": 5,
          "pasteQuietPeriodMs": 20, "pasteSettleTimeoutMs": 2000, "submitVerifyMs": 20, "maxSubmitAttempts": 2,
          "readyPattern": "(?is)esc to interrupt",
          "rules": []
        }
        """;

        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        terminal.CurrentSnapshot = "PS C:\\ws>";
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        var run = CreateOrchestratorWithRules(longCeilingRules).RunAsync(CreateRequest(terminal, signal), cts.Token);

        await WaitUntil(() => terminal.LauncherNonce is not null, cts.Token);
        terminal.EmitOutput();

        // Act
        signal.Signal();

        // Assert - the phase gives up its session promptly, and starts no agent on the way out.
        await WaitUntil(
            () => _progress.Any(p => p.Phase == WorkflowPhase.Specification && p.Status == PhaseStatus.Completed),
            cts.Token);

        // Scoped to the phase the user completed by hand: once it ends, the run chains on and
        // the NEXT phase legitimately pastes its own prompt.
        Assert.DoesNotContain(
            terminal.Pasted,
            p => p.Contains("PROMPT Specification", StringComparison.Ordinal));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SettleLoop_ManualSignalDuringStall_DoesNotPaste()
    {
        // Arrange
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        terminal.CurrentSnapshot = "PS C:\\ws>";
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        var run = CreateOrchestratorWithRules(EvidenceRules).RunAsync(CreateRequest(terminal, signal), cts.Token);

        await WaitUntil(() => terminal.LauncherNonce is not null, cts.Token);
        terminal.SimulateLauncherExit();
        await Task.Delay(1200, cts.Token);

        // Act - the user gives up on starting a launcher and completes the phase by hand.
        signal.Signal();

        // Assert
        await WaitUntil(
            () => _progress.Any(p => p.Phase == WorkflowPhase.Specification && p.Status == PhaseStatus.Completed),
            cts.Token);

        // Scoped to the phase the user completed by hand: once it ends, the run chains on and
        // the NEXT phase legitimately pastes its own prompt.
        Assert.DoesNotContain(
            terminal.Pasted,
            p => p.Contains("PROMPT Specification", StringComparison.Ordinal));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SettleLoop_ManualSignalSetBeforeReadiness_DoesNotPaste()
    {
        // Arrange - the manual signal is authoritative, never a race winner: once it is set, a
        // launcher appearing afterwards must not revive the paste.
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        terminal.CurrentSnapshot = "PS C:\\ws>";
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        var run = CreateOrchestratorWithRules(EvidenceRules).RunAsync(CreateRequest(terminal, signal), cts.Token);

        await WaitUntil(() => terminal.LauncherNonce is not null, cts.Token);
        terminal.SimulateLauncherExit();
        await Task.Delay(1000, cts.Token);

        // Act
        signal.Signal();
        terminal.CurrentSnapshot = "esc to interrupt";
        terminal.SimulateLauncherRelaunch();
        terminal.EmitOutput();

        // Assert
        await Task.Delay(1500, cts.Token);

        // Scoped to the phase the user completed by hand: once it ends, the run chains on and
        // the NEXT phase legitimately pastes its own prompt.
        Assert.DoesNotContain(
            terminal.Pasted,
            p => p.Contains("PROMPT Specification", StringComparison.Ordinal));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SettleLoop_ReturnMarkerObserved_ReportsTheStallThenClearsIt()
    {
        // Arrange
        var reports = new List<string?>();
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        terminal.CurrentSnapshot = "PS C:\\ws>";
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        var request = CreateRequest(
            terminal,
            signal,
            new InlineProgress<string?>(m =>
            {
                lock (reports)
                {
                    reports.Add(m);
                }
            }));

        var run = CreateOrchestratorWithRules(EvidenceRules).RunAsync(request, cts.Token);

        await WaitUntil(() => terminal.LauncherNonce is not null, cts.Token);
        terminal.SimulateLauncherExit();

        // Act
        await WaitUntil(
            () =>
            {
                lock (reports)
                {
                    return reports.Count >= 1;
                }
            },
            cts.Token);

        // Assert - exactly one reason, reported once, and never a null that would clear an
        // unrelated message the tab is already showing.
        lock (reports)
        {
            Assert.Single(reports);
            Assert.False(string.IsNullOrWhiteSpace(reports[0]));
        }

        // The launcher comes up: the session clears its own status and pastes.
        terminal.CurrentSnapshot = "esc to interrupt";
        terminal.SimulateLauncherRelaunch();
        terminal.EmitOutput();

        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);

        lock (reports)
        {
            Assert.Null(reports[^1]);
        }

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SettleLoop_CancelledDuringTheStall_ClearsTheLauncherStatus()
    {
        // Arrange
        var reports = new List<string?>();
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        terminal.CurrentSnapshot = "PS C:\\ws>";
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        var request = CreateRequest(
            terminal,
            signal,
            new InlineProgress<string?>(m =>
            {
                lock (reports)
                {
                    reports.Add(m);
                }
            }));

        var run = CreateOrchestratorWithRules(EvidenceRules).RunAsync(request, cts.Token);

        await WaitUntil(() => terminal.LauncherNonce is not null, cts.Token);
        terminal.SimulateLauncherExit();
        await WaitUntil(
            () =>
            {
                lock (reports)
                {
                    return reports.Count >= 1;
                }
            },
            cts.Token);

        // Act
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        // Assert - a stall reason must not outlive the run that produced it.
        lock (reports)
        {
            Assert.Null(reports[^1]);
        }
    }

    private WorkflowRunRequest CreateRequest(
        FakeTerminalController terminal,
        ManualPhaseSignal signal,
        IProgress<string?>? launcherStatus = null) =>
        new(_paths, "Beschreibung", terminal, signal, new InlineProgress<PhaseProgress>(p =>
        {
            lock (_progress)
            {
                _progress.Add(p);
            }
        }),
        LauncherStatus: launcherStatus);

    private void WriteArtifacts(params string[] paths)
    {
        foreach (var path in paths)
        {
            File.WriteAllText(path, "content-" + Guid.NewGuid().ToString("N"));
        }
    }

    /// <summary>True once the given phase's test prompt (<c>PROMPT {Phase} ...</c>) has been pasted.</summary>
    private static bool PromptPasted(FakeTerminalController terminal, WorkflowPhase phase) =>
        terminal.Pasted.ToArray().Any(p => p.Contains($"PROMPT {phase} ", StringComparison.Ordinal));

    /// <summary>
    /// Plays the simplifier: waits until its prompt was pasted - so the orchestrator's deletion of a
    /// stale report is already behind it - and then writes the report that completes the phase.
    /// </summary>
    private async Task CompleteSimplifierAsync(FakeTerminalController terminal, CancellationToken cancellationToken)
    {
        await WaitUntil(() => PromptPasted(terminal, WorkflowPhase.Simplifier), cancellationToken);
        WriteArtifacts(_paths.SimplifiedAbsolute);
    }

    /// <summary>
    /// Runs past Specification with a stale simplifier report the orchestrator cannot delete, and
    /// asserts that the run stops before the simplifier session: no completion, no Review, and the
    /// journal leaves Vereinfachen Active so the task can be resumed once the file is released.
    /// </summary>
    private async Task AssertUndeletableSimplifierReportStopsThePhaseAsync()
    {
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, signal), cts.Token);

        await WaitForPhaseAsync(WorkflowPhase.Specification, cts.Token);
        WriteArtifacts(_paths.SpecAbsolute, _paths.PlanAbsolute);

        var error = await Assert.ThrowsAsync<IOException>(() => run);
        Assert.Contains(_paths.SimplifiedAbsolute, error.Message, StringComparison.Ordinal);
        Assert.Single(terminal.StartedSessions);
        lock (_progress)
        {
            Assert.DoesNotContain(
                _progress, p => p.Phase == WorkflowPhase.Simplifier && p.Status == PhaseStatus.Completed);
            Assert.DoesNotContain(_progress, p => p.Phase == WorkflowPhase.Review);
        }

        Assert.Equal(
            PhaseStatus.Active,
            _state.Recorded.Last(r => r.Phase == WorkflowPhase.Simplifier).Status);
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
        // The launcher is wrapped so the shell reports when it starts and when it returns;
        // the bare command is no longer written on its own.
        Assert.Contains("try { yo } finally {", terminal.Sent[1], StringComparison.Ordinal);
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
    public async Task Phases_ChainThroughAllFiveStations()
    {
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, signal), cts.Token);

        // Phase 1 -> spec and plan appear.
        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);
        WriteArtifacts(_paths.SpecAbsolute, _paths.PlanAbsolute);

        // Phase 2 (Vereinfachen) -> its report appears, written as its last action.
        await WaitUntil(() => terminal.StartedSessions.Count >= 2, cts.Token);
        await CompleteSimplifierAsync(terminal, cts.Token);

        // Phase 3 -> review appears.
        await WaitUntil(() => terminal.StartedSessions.Count >= 3, cts.Token);
        Assert.Contains(
            @"try { & ""$env:LOCALAPPDATA\Programs\OpenAI\Codex\bin\codex.exe"" --yolo --no-daemon } finally {",
            terminal.Sent.Last(s => s.Contains("codex", StringComparison.Ordinal)),
            StringComparison.Ordinal);
        WriteArtifacts(_paths.ReviewAbsolute);

        // Phase 4 -> BOTH spec and plan content change (CompletionRule.AllContentChanged).
        await WaitUntil(() => terminal.StartedSessions.Count >= 4, cts.Token);
        await Task.Delay(80, cts.Token);
        WriteArtifacts(_paths.SpecAbsolute, _paths.PlanAbsolute);

        // Phase 5 -> waits for the manual signal.
        await WaitUntil(() => terminal.StartedSessions.Count >= 5, cts.Token);

        // Waited for, not slept past: the manual signal now outranks the settle loop, so a
        // fixed delay shorter than this fixture's 2000 ms ceiling would signal first and the
        // phase would correctly decline to start an agent - leaving this test asserting the
        // old behaviour of pasting a prompt after the user asked to stop.
        await WaitUntil(() => terminal.Pasted.Count >= 5, cts.Token);
        Assert.False(run.IsCompleted);

        signal.Signal();
        await run;

        Assert.Equal(5, terminal.StartedSessions.Count);
        Assert.Equal(5, terminal.Pasted.Count);

        lock (_progress)
        {
            Assert.Contains(_progress, p => p.Phase == WorkflowPhase.Specification && p.Status == PhaseStatus.Completed);
            Assert.Contains(_progress, p => p.Phase == WorkflowPhase.Simplifier && p.Status == PhaseStatus.Completed);
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
    public async Task RunAsync_DeletesAStaleDoneMarkerBeforeImplementationWaits()
    {
        await File.WriteAllTextAsync(_paths.DoneAbsolute, "left over from a previous run");

        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, signal), cts.Token);

        // Satisfy every earlier phase so the run reaches the implementation.
        await WaitForPhaseAsync(WorkflowPhase.Specification, cts.Token);
        WriteArtifacts(_paths.SpecAbsolute, _paths.PlanAbsolute);
        await WaitForPhaseAsync(WorkflowPhase.Simplifier, cts.Token);
        await CompleteSimplifierAsync(terminal, cts.Token);
        await WaitForPhaseAsync(WorkflowPhase.Review, cts.Token);
        WriteArtifacts(_paths.ReviewAbsolute);
        await WaitForPhaseAsync(WorkflowPhase.ResolveReview, cts.Token);
        WriteArtifacts(_paths.SpecAbsolute, _paths.PlanAbsolute);
        await WaitForPhaseAsync(WorkflowPhase.Implementation, cts.Token);

        // The stale marker must be gone, so the implementation is still waiting.
        Assert.False(File.Exists(_paths.DoneAbsolute));
        Assert.DoesNotContain(
            _progress, p => p.Phase == WorkflowPhase.Implementation && p.Status == PhaseStatus.Completed);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task RunAsync_StaleSimplifierReportFromAnEarlierRun_IsDeletedBeforeTheSimplifierSessionStarts()
    {
        // Arrange - a report left by an earlier run would satisfy the FilesExist watcher at once.
        await File.WriteAllTextAsync(_paths.SimplifiedAbsolute, "left over from a previous run");
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, signal), cts.Token);

        // Act
        await WaitForPhaseAsync(WorkflowPhase.Specification, cts.Token);
        WriteArtifacts(_paths.SpecAbsolute, _paths.PlanAbsolute);
        await WaitForPhaseAsync(WorkflowPhase.Simplifier, cts.Token);
        await WaitUntil(() => PromptPasted(terminal, WorkflowPhase.Simplifier), cts.Token);
        await Task.Delay(300, cts.Token);

        // Assert - the stale report is gone, so the simplifier is still waiting for its own.
        Assert.False(File.Exists(_paths.SimplifiedAbsolute));
        Assert.Equal(2, terminal.StartedSessions.Count);
        lock (_progress)
        {
            Assert.DoesNotContain(
                _progress, p => p.Phase == WorkflowPhase.Simplifier && p.Status == PhaseStatus.Completed);
            Assert.DoesNotContain(_progress, p => p.Phase == WorkflowPhase.Review);
        }

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task RunAsync_StaleSimplifierReportIsReadOnly_FailsWithoutCompletingOrStartingReview()
    {
        // Arrange - a read-only report cannot be deleted, so it would complete the phase at once.
        await File.WriteAllTextAsync(_paths.SimplifiedAbsolute, "left over from a previous run");
        File.SetAttributes(_paths.SimplifiedAbsolute, FileAttributes.ReadOnly);

        try
        {
            // Act & Assert
            await AssertUndeletableSimplifierReportStopsThePhaseAsync();
        }
        finally
        {
            File.SetAttributes(_paths.SimplifiedAbsolute, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task RunAsync_StaleSimplifierReportIsLocked_FailsWithoutCompletingOrStartingReview()
    {
        // Arrange - a report held open without delete sharing cannot be deleted either.
        await File.WriteAllTextAsync(_paths.SimplifiedAbsolute, "left over from a previous run");
        using var lockHandle = new FileStream(_paths.SimplifiedAbsolute, FileMode.Open, FileAccess.Read, FileShare.None);

        // Act & Assert
        await AssertUndeletableSimplifierReportStopsThePhaseAsync();
    }

    [Fact]
    public async Task RunAsync_SimplifierRewritesSpecAndPlanWithoutItsReport_StaysActive()
    {
        // Arrange - the simplifier edits spec and plan in place over many writes; only its report,
        // written last, may end the phase.
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = CreateOrchestrator().RunAsync(CreateRequest(terminal, signal), cts.Token);
        await WaitForPhaseAsync(WorkflowPhase.Specification, cts.Token);
        WriteArtifacts(_paths.SpecAbsolute, _paths.PlanAbsolute);
        await WaitUntil(() => PromptPasted(terminal, WorkflowPhase.Simplifier), cts.Token);

        // Act
        await Task.Delay(80, cts.Token);
        WriteArtifacts(_paths.SpecAbsolute, _paths.PlanAbsolute);
        await Task.Delay(500, cts.Token);

        // Assert
        Assert.Equal(2, terminal.StartedSessions.Count);
        lock (_progress)
        {
            Assert.DoesNotContain(
                _progress, p => p.Phase == WorkflowPhase.Simplifier && p.Status == PhaseStatus.Completed);
            Assert.DoesNotContain(_progress, p => p.Phase == WorkflowPhase.Review);
        }

        // Positive control: the report alone ends the phase and the review starts.
        WriteArtifacts(_paths.SimplifiedAbsolute);
        await WaitForPhaseAsync(WorkflowPhase.Review, cts.Token);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task RunAsync_StartPhaseResolveReview_SkipsEveryEarlierPhase()
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
            Assert.DoesNotContain(_progress, p => p.Phase == WorkflowPhase.Simplifier);
            Assert.DoesNotContain(_progress, p => p.Phase == WorkflowPhase.Review);
        }

        Assert.Single(terminal.StartedSessions);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task RunAsync_ClearsTheJournalTailFromTheStartPhaseBeforeRunning()
    {
        // A previously finished task, re-entered at Review umsetzen.
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
        Assert.Equal(PhaseStatus.Completed, journal.Phases[(int)WorkflowPhase.Specification].Status);
        Assert.Equal(PhaseStatus.Completed, journal.Phases[(int)WorkflowPhase.Simplifier].Status);
        Assert.Equal(PhaseStatus.Completed, journal.Phases[(int)WorkflowPhase.Review].Status);

        // The tail was cleared before anything ran; Review umsetzen is Active because it is running
        // now and the implementation must NOT still read Completed (SPEC 7.4, D24).
        Assert.Equal(PhaseStatus.Active, journal.Phases[(int)WorkflowPhase.ResolveReview].Status);
        Assert.Equal(PhaseStatus.Pending, journal.Phases[(int)WorkflowPhase.Implementation].Status);
        Assert.Null(journal.Phases[(int)WorkflowPhase.Implementation].CompletedUtc);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task RunAsync_StartPhaseImplementation_KeepsTheSkippedReviewPhasesInTheJournal()
    {
        // Arrange - Workflow_LoadFix spec 7.2: 'Implementierung beginnen' records the skips and then
        // starts at the implementation; ClearPhasesFrom must leave everything before the start phase alone.
        var skipped = new TaskState { TaskDescription = "d", UpdatedUtc = DateTimeOffset.UtcNow };
        skipped.Phases.Add(new TaskPhaseState(WorkflowPhase.Specification, PhaseStatus.Completed, DateTimeOffset.UtcNow));
        skipped.Phases.Add(new TaskPhaseState(WorkflowPhase.Simplifier, PhaseStatus.Skipped, DateTimeOffset.UtcNow));
        skipped.Phases.Add(new TaskPhaseState(WorkflowPhase.Review, PhaseStatus.Skipped, DateTimeOffset.UtcNow));
        skipped.Phases.Add(new TaskPhaseState(WorkflowPhase.ResolveReview, PhaseStatus.Skipped, DateTimeOffset.UtcNow));
        skipped.Phases.Add(new TaskPhaseState(WorkflowPhase.Implementation, PhaseStatus.Pending, null));
        _state.Seed(_paths, skipped);

        var terminal = new FakeTerminalController();
        var signal = new ManualPhaseSignal();
        terminal.ReadyGate.TrySetResult();
        var request = CreateRequest(terminal, signal) with { StartPhase = WorkflowPhase.Implementation };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        var run = CreateOrchestrator().RunAsync(request, cts.Token);
        await WaitForPhaseAsync(WorkflowPhase.Implementation, cts.Token);

        // Assert
        var journal = _state.TryLoad(_paths)!;
        Assert.Equal(PhaseStatus.Completed, journal.Phases[(int)WorkflowPhase.Specification].Status);
        Assert.Equal(PhaseStatus.Skipped, journal.Phases[(int)WorkflowPhase.Simplifier].Status);
        Assert.Equal(PhaseStatus.Skipped, journal.Phases[(int)WorkflowPhase.Review].Status);
        Assert.Equal(PhaseStatus.Skipped, journal.Phases[(int)WorkflowPhase.ResolveReview].Status);
        Assert.Equal(PhaseStatus.Active, journal.Phases[(int)WorkflowPhase.Implementation].Status);

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
        await WaitForPhaseAsync(WorkflowPhase.Simplifier, cts.Token);
        await CompleteSimplifierAsync(terminal, cts.Token);
        await WaitForPhaseAsync(WorkflowPhase.Review, cts.Token);

        Assert.Equal(
            new[]
            {
                (WorkflowPhase.Specification, PhaseStatus.Active),
                (WorkflowPhase.Specification, PhaseStatus.Completed),
                (WorkflowPhase.Simplifier, PhaseStatus.Active),
                (WorkflowPhase.Simplifier, PhaseStatus.Completed),
                (WorkflowPhase.Review, PhaseStatus.Active),
            },
            _state.Recorded.Take(5));

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

    // --- Positive submission evidence (Claude Code) ---------------------------------------
    //
    // Claude Code reads a chunk as the Return key only when the chunk is a single "\r". Carriage
    // returns that queue up behind a paste still on its way arrive together and are inserted as
    // literal line breaks - the 2026-10-06 failure of Workflow_LoadFix ST-001. These tests pin that
    // the evidence path writes at most one CR, only once the paste has demonstrably landed, and
    // that nothing but the working footer counts as proof of submission.

    private const string SubmitEvidenceRules = """
    {
      "version": 2, "quietPeriodMs": 20, "settleTimeoutMs": 20000, "maxAnswersPerPhase": 5,
      "pasteQuietPeriodMs": 20, "pasteSettleTimeoutMs": 600, "submitVerifyMs": 20, "maxSubmitAttempts": 8,
      "submitAfterPasteLandedMs": 50, "submittedVerifyTimeoutMs": 300,
      "readyPattern": "(?is)for shortcuts",
      "pastePendingPattern": "(?is)\\[Pasted text #\\d+",
      "submittedPattern": "(?is)esc to interrupt",
      "rules": []
    }
    """;

    private const string IdleScreen = "? for shortcuts";

    private const string ChipScreen = "❯ [Pasted text #1 +578 lines]\n? for shortcuts";

    private const string WorkingScreen = "✻ Pondering… (esc to interrupt)";

    /// <summary>A fragment only the unconfirmed-submission warning contains.</summary>
    private const string UnconfirmedFragment = "Absenden des Prompts";

    private static InlineProgress<string?> RecordInto(List<string?> reports) => new(message =>
    {
        lock (reports)
        {
            reports.Add(message);
        }
    });

    private static bool HasUnconfirmedReport(List<string?> reports)
    {
        lock (reports)
        {
            return reports.Any(r => r is not null && r.Contains(UnconfirmedFragment, StringComparison.Ordinal));
        }
    }

    private WorkflowRunRequest CreatePromptRequest(
        FakeTerminalController terminal,
        ManualPhaseSignal signal,
        List<string?> reports,
        WorkflowPhase startPhase = WorkflowPhase.Specification) =>
        CreateRequest(terminal, signal, RecordInto(reports)) with { StartPhase = startPhase };

    /// <summary>Starts a run and brings its first launcher to the ready screen.</summary>
    private async Task<Task> StartPromptRunAsync(
        FakeTerminalController terminal,
        WorkflowRunRequest request,
        string rules,
        CancellationToken cancellationToken)
    {
        terminal.ReadyGate.TrySetResult();
        terminal.CurrentSnapshot ??= IdleScreen;

        var run = CreateOrchestratorWithRules(rules).RunAsync(request, cancellationToken);

        // The launcher command carries the nonce and is written after StartSession has reset the
        // output counter, so output emitted from here on opens Gate A for this very session.
        await WaitUntil(() => terminal.LauncherNonce is not null, cancellationToken);
        terminal.EmitOutput();

        return run;
    }

    [Fact]
    public async Task SendPrompt_PositiveEvidence_PasteNotLanded_SendsNoCarriageReturnAndReportsUnconfirmed()
    {
        // Arrange - nothing reacts to the paste: no chip and no output, i.e. it is still on its way.
        var terminal = new FakeTerminalController();
        var signal = new ManualPhaseSignal();
        var reports = new List<string?>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // Act
        var run = await StartPromptRunAsync(
            terminal, CreatePromptRequest(terminal, signal, reports), SubmitEvidenceRules, cts.Token);
        await WaitUntil(() => HasUnconfirmedReport(reports), cts.Token);

        // Assert - absence of output is not evidence that it is safe to submit.
        Assert.Single(terminal.Pasted);
        Assert.Equal(0, terminal.CarriageReturnCount);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SendPrompt_PositiveEvidence_ChipLanded_SubmittedOnFirstCarriageReturn_SendsExactlyOne()
    {
        // Arrange
        var terminal = new FakeTerminalController();
        terminal.OnPaste = () =>
        {
            terminal.CurrentSnapshot = ChipScreen;
            terminal.EmitOutput();
        };
        terminal.OnCarriageReturn = () =>
        {
            terminal.CurrentSnapshot = WorkingScreen;
            terminal.EmitOutput();
        };
        var signal = new ManualPhaseSignal();
        var reports = new List<string?>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var run = await StartPromptRunAsync(
            terminal, CreatePromptRequest(terminal, signal, reports), SubmitEvidenceRules, cts.Token);
        await WaitUntil(() => terminal.CarriageReturnCount >= 1, cts.Token);

        // Act - the phase can end on its artefacts only after the prompt step has returned, so the
        // next session starting is proof that the submit step is over.
        WriteArtifacts(_paths.SpecAbsolute, _paths.PlanAbsolute);
        await WaitUntil(() => terminal.StartedSessions.Count >= 2, cts.Token);

        // Assert
        Assert.Equal(1, terminal.CarriageReturnCount);
        Assert.False(HasUnconfirmedReport(reports));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SendPrompt_PositiveEvidence_CarriageReturnUnconfirmed_NeverSendsASecond()
    {
        // Arrange - the CR is read and the chip and its hint disappear (exactly what stray line
        // breaks look like), but Claude never starts working.
        var terminal = new FakeTerminalController();
        terminal.OnPaste = () =>
        {
            terminal.CurrentSnapshot = ChipScreen;
            terminal.EmitOutput();
        };
        terminal.OnCarriageReturn = () =>
        {
            terminal.CurrentSnapshot = IdleScreen;
            terminal.EmitOutput();
        };
        var signal = new ManualPhaseSignal();
        var reports = new List<string?>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // Act
        var run = await StartPromptRunAsync(
            terminal, CreatePromptRequest(terminal, signal, reports), SubmitEvidenceRules, cts.Token);
        await WaitUntil(() => HasUnconfirmedReport(reports), cts.Token);

        // Assert - output, quietness and a vanished chip are none of them an acknowledgement.
        Assert.Equal(1, terminal.CarriageReturnCount);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SendPrompt_PositiveEvidence_DelayedUnrelatedOutputAfterCarriageReturn_NeverSendsASecond()
    {
        // Arrange - output arrives a little after the CR while the chip stays on screen. A
        // "consumed" test built on OutputCount would take that for the CR being read and write a
        // second one, which could then merge with the first into a literal "\r\r".
        var terminal = new FakeTerminalController();
        terminal.OnPaste = () =>
        {
            terminal.CurrentSnapshot = ChipScreen;
            terminal.EmitOutput();
        };
        terminal.OnCarriageReturn = () => _ = Task.Run(async () =>
        {
            await Task.Delay(40);
            terminal.EmitOutput();
        });
        var signal = new ManualPhaseSignal();
        var reports = new List<string?>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // Act
        var run = await StartPromptRunAsync(
            terminal, CreatePromptRequest(terminal, signal, reports), SubmitEvidenceRules, cts.Token);
        await WaitUntil(() => HasUnconfirmedReport(reports), cts.Token);

        // Assert
        Assert.Equal(1, terminal.CarriageReturnCount);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SendPrompt_PositiveEvidence_BusyBaseline_DoesNotPasteOrSubmit()
    {
        // Arrange - the launcher is ready but already shows the working footer, so no false-to-true
        // transition could ever prove that THIS prompt started.
        var terminal = new FakeTerminalController { CurrentSnapshot = IdleScreen + "\n" + WorkingScreen };
        var signal = new ManualPhaseSignal();
        var reports = new List<string?>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // Act
        var run = await StartPromptRunAsync(
            terminal, CreatePromptRequest(terminal, signal, reports), SubmitEvidenceRules, cts.Token);
        await WaitUntil(() => HasUnconfirmedReport(reports), cts.Token);

        // Assert - and no fallback to the repeated legacy CR loop either.
        Assert.Empty(terminal.Pasted);
        Assert.Equal(0, terminal.CarriageReturnCount);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SendPrompt_PositiveEvidence_PatternAppearsBeforeCarriageReturn_DoesNotSubmit()
    {
        // Arrange - a short prompt is not collapsed, so its own text is on screen, and here it
        // contains the very words the working footer uses.
        var terminal = new FakeTerminalController();
        terminal.OnPaste = () =>
        {
            terminal.CurrentSnapshot = "❯ never press esc to interrupt the build\n" + IdleScreen;
            terminal.EmitOutput();
        };
        var signal = new ManualPhaseSignal();
        var reports = new List<string?>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // Act
        var run = await StartPromptRunAsync(
            terminal, CreatePromptRequest(terminal, signal, reports), SubmitEvidenceRules, cts.Token);
        await WaitUntil(() => HasUnconfirmedReport(reports), cts.Token);

        // Assert - a later match could not be attributed to this prompt, so no CR is written.
        Assert.Single(terminal.Pasted);
        Assert.Equal(0, terminal.CarriageReturnCount);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SendPrompt_PositiveEvidence_ShortPromptUsesOutputAndQuietAsLandingOnly()
    {
        // Arrange - no chip; the paste's own output is emitted synchronously INSIDE SendPaste, which
        // only counts as landing if the output baseline was captured before that call.
        var terminal = new FakeTerminalController();
        terminal.OnPaste = () =>
        {
            terminal.CurrentSnapshot = "❯ kurzer Prompt\n" + IdleScreen;
            terminal.EmitOutput();
        };
        terminal.OnCarriageReturn = () =>
        {
            terminal.CurrentSnapshot = WorkingScreen;
            terminal.EmitOutput();
        };
        var signal = new ManualPhaseSignal();
        var reports = new List<string?>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var run = await StartPromptRunAsync(
            terminal, CreatePromptRequest(terminal, signal, reports), SubmitEvidenceRules, cts.Token);
        await WaitUntil(() => terminal.CarriageReturnCount >= 1, cts.Token);

        // Act
        WriteArtifacts(_paths.SpecAbsolute, _paths.PlanAbsolute);
        await WaitUntil(() => terminal.StartedSessions.Count >= 2, cts.Token);

        // Assert - output and quiet permitted the one CR; the working footer confirmed it.
        Assert.Equal(1, terminal.CarriageReturnCount);
        Assert.False(HasUnconfirmedReport(reports));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SendPrompt_PositiveEvidence_ManualCompletionDuringLandingWait_StopsPromptWorkImmediately()
    {
        // Arrange - a landing cap far longer than the test is willing to wait.
        var rules = SubmitEvidenceRules.Replace(
            "\"pasteSettleTimeoutMs\": 600", "\"pasteSettleTimeoutMs\": 15000", StringComparison.Ordinal);
        var terminal = new FakeTerminalController();
        var signal = new ManualPhaseSignal();
        var reports = new List<string?>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var run = await StartPromptRunAsync(
            terminal, CreatePromptRequest(terminal, signal, reports), rules, cts.Token);
        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);
        var clock = Stopwatch.StartNew();

        // Act - 'Task abschliessen' while the paste has not landed.
        signal.Signal();
        await WaitUntil(() => terminal.StartedSessions.Count >= 2, cts.Token);

        // Assert - the phase ended on the user's signal, not on the landing cap, with no later CR
        // and no warning about a submission the user has just overruled.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"The landing wait ran on for {clock.Elapsed}.");
        Assert.Equal(0, terminal.CarriageReturnCount);
        Assert.False(HasUnconfirmedReport(reports));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SendPrompt_PositiveEvidence_InvalidSubmittedPatternFailsSafeAfterAtMostOneCarriageReturn()
    {
        // Arrange - a malformed but non-empty pattern must not select the repeated legacy loop.
        var rules = SubmitEvidenceRules.Replace(
            "\"(?is)esc to interrupt\"", "\"([unclosed\"", StringComparison.Ordinal);
        var terminal = new FakeTerminalController();
        terminal.OnPaste = () =>
        {
            terminal.CurrentSnapshot = ChipScreen;
            terminal.EmitOutput();
        };
        terminal.OnCarriageReturn = () =>
        {
            terminal.CurrentSnapshot = WorkingScreen;
            terminal.EmitOutput();
        };
        var signal = new ManualPhaseSignal();
        var reports = new List<string?>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // Act
        var run = await StartPromptRunAsync(
            terminal, CreatePromptRequest(terminal, signal, reports), rules, cts.Token);
        await WaitUntil(() => HasUnconfirmedReport(reports), cts.Token);

        // Assert
        Assert.Equal(1, terminal.CarriageReturnCount);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    // --- Codex: Enter retried until evidence (WF_REVIEWENTER) ------------------------------
    //
    // Codex redraws on every Enter, whether it submits or not, so output after a carriage return
    // proves nothing. The old loop stopped after one such CR and left the review prompt unsent.

    private const string CodexEvidenceRules = """
    {
      "version": 2, "quietPeriodMs": 20, "settleTimeoutMs": 20000, "maxAnswersPerPhase": 5,
      "pasteQuietPeriodMs": 20, "pasteSettleTimeoutMs": 3000, "submitVerifyMs": 60, "maxSubmitAttempts": 4,
      "readyPattern": "(?is)(for shortcuts|esc to interrupt)",
      "pastePendingPattern": "(?is)(\\[Pasted text #\\d+|\\[Pasted Content \\d+ chars\\])",
      "submittedPattern": "(?is)esc to interrupt",
      "rules": []
    }
    """;

    private const string CodexIdleScreen = "› Ask Codex to do anything\n? for shortcuts · 100% context left";

    private const string CodexChipScreen = "› [Pasted Content 1170 chars]\n100% context left";

    private const string CodexTextScreen = "› /review Du bist ein adversarialer Reviewer.\n100% context left";

    private const string CodexWorkingScreen = "• Working (0s • esc to interrupt)\n› Ask Codex to do anything";

    private const string CodexStartupScreen = "• Starting MCP servers (0/2): context7 (0s • esc to interrupt)\n› Ask Codex to do anything";

    /// <summary>The startup footer back on screen with the pasted prompt as composer text and no chip.</summary>
    private const string CodexStartupTextScreen =
        "• Starting MCP servers (1/2): context7 (0s • esc to interrupt)\n› /review Du bist ein adversarialer Reviewer.";

    /// <summary>Bare carriage returns written before the second session's launcher command.</summary>
    /// <remarks>The next phase's session may write its own CR; only the Review session's count.</remarks>
    private static int CarriageReturnsInFirstSession(FakeTerminalController terminal)
    {
        var launches = terminal.Sent
            .Select((text, index) => (text, index))
            .Where(entry => entry.text.Contains("WF-RUN-", StringComparison.Ordinal))
            .Select(entry => entry.index)
            .ToList();
        var end = launches.Count >= 2 ? launches[1] : terminal.Sent.Count;

        return terminal.Sent.Take(end).Count(text => text == "\r");
    }

    [Fact]
    public async Task SendPrompt_RetryUntilEvidence_NoChipFirstCarriageReturnOnlyRedraws_SendsASecond()
    {
        // Arrange - the prompt stands in the composer as text; the first Enter only redraws, which
        // the old loop took for a submit, and only the second starts Codex working.
        var terminal = new FakeTerminalController { CurrentSnapshot = CodexIdleScreen };
        terminal.OnPaste = () =>
        {
            terminal.CurrentSnapshot = CodexTextScreen;
            terminal.EmitOutput();
        };
        terminal.OnCarriageReturn = () =>
        {
            if (terminal.CarriageReturnCount == 1)
            {
                terminal.EmitOutput();
                return;
            }

            terminal.CurrentSnapshot = CodexWorkingScreen;
            terminal.EmitOutput();
        };
        var signal = new ManualPhaseSignal();
        var reports = new List<string?>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var run = await StartPromptRunAsync(
            terminal, CreatePromptRequest(terminal, signal, reports, WorkflowPhase.Review), CodexEvidenceRules, cts.Token);
        await WaitUntil(() => terminal.CarriageReturnCount >= 2, cts.Token);

        // Act
        WriteArtifacts(_paths.ReviewAbsolute);
        await WaitUntil(() => terminal.StartedSessions.Count >= 2, cts.Token);

        // Assert
        Assert.Equal(2, CarriageReturnsInFirstSession(terminal));
        Assert.False(HasUnconfirmedReport(reports));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SendPrompt_RetryUntilEvidence_ChipFirstCarriageReturnOnlyRedraws_SendsASecond()
    {
        // Arrange - the first Enter leaves the chip in place; the second takes it off a screen that
        // still shows Codex's own footer.
        var terminal = new FakeTerminalController { CurrentSnapshot = CodexIdleScreen };
        terminal.OnPaste = () =>
        {
            terminal.CurrentSnapshot = CodexChipScreen;
            terminal.EmitOutput();
        };
        terminal.OnCarriageReturn = () =>
        {
            if (terminal.CarriageReturnCount == 1)
            {
                terminal.EmitOutput();
                return;
            }

            terminal.CurrentSnapshot = CodexIdleScreen;
            terminal.EmitOutput();
        };
        var signal = new ManualPhaseSignal();
        var reports = new List<string?>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var run = await StartPromptRunAsync(
            terminal, CreatePromptRequest(terminal, signal, reports, WorkflowPhase.Review), CodexEvidenceRules, cts.Token);
        await WaitUntil(() => terminal.CarriageReturnCount >= 2, cts.Token);

        // Act
        WriteArtifacts(_paths.ReviewAbsolute);
        await WaitUntil(() => terminal.StartedSessions.Count >= 2, cts.Token);

        // Assert
        Assert.Equal(2, CarriageReturnsInFirstSession(terminal));
        Assert.False(HasUnconfirmedReport(reports));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SendPrompt_RetryUntilEvidence_WorkingFooterAfterFirstCarriageReturn_SendsExactlyOne()
    {
        // Arrange
        var terminal = new FakeTerminalController { CurrentSnapshot = CodexIdleScreen };
        terminal.OnPaste = () =>
        {
            terminal.CurrentSnapshot = CodexChipScreen;
            terminal.EmitOutput();
        };
        terminal.OnCarriageReturn = () =>
        {
            terminal.CurrentSnapshot = CodexWorkingScreen;
            terminal.EmitOutput();
        };
        var signal = new ManualPhaseSignal();
        var reports = new List<string?>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var run = await StartPromptRunAsync(
            terminal, CreatePromptRequest(terminal, signal, reports, WorkflowPhase.Review), CodexEvidenceRules, cts.Token);
        await WaitUntil(() => terminal.CarriageReturnCount >= 1, cts.Token);

        // Act
        WriteArtifacts(_paths.ReviewAbsolute);
        await WaitUntil(() => terminal.StartedSessions.Count >= 2, cts.Token);

        // Assert - evidence after the first Enter ends the loop; no retry follows it.
        Assert.Equal(1, CarriageReturnsInFirstSession(terminal));
        Assert.False(HasUnconfirmedReport(reports));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SendPrompt_RetryUntilEvidence_NeverAccepted_StopsAtMaxAttemptsAndWarns()
    {
        // Arrange - every Enter redraws, none takes the chip.
        var terminal = new FakeTerminalController { CurrentSnapshot = CodexIdleScreen };
        terminal.OnPaste = () =>
        {
            terminal.CurrentSnapshot = CodexChipScreen;
            terminal.EmitOutput();
        };
        terminal.OnCarriageReturn = terminal.EmitOutput;
        var signal = new ManualPhaseSignal();
        var reports = new List<string?>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // Act
        var run = await StartPromptRunAsync(
            terminal, CreatePromptRequest(terminal, signal, reports, WorkflowPhase.Review), CodexEvidenceRules, cts.Token);
        await WaitUntil(() => HasUnconfirmedReport(reports), cts.Token);

        // Assert - bounded by maxSubmitAttempts, then the user is told.
        Assert.Equal(4, CarriageReturnsInFirstSession(terminal));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SendPrompt_RetryUntilEvidence_StartupFooterOnScreen_PastesOnlyOnceItClears()
    {
        // Arrange - Codex is still starting its MCP servers and shows the same footer it works with.
        var terminal = new FakeTerminalController { CurrentSnapshot = CodexStartupScreen };
        terminal.OnPaste = () =>
        {
            terminal.CurrentSnapshot = CodexChipScreen;
            terminal.EmitOutput();
        };
        terminal.OnCarriageReturn = () =>
        {
            terminal.CurrentSnapshot = CodexWorkingScreen;
            terminal.EmitOutput();
        };
        var signal = new ManualPhaseSignal();
        var reports = new List<string?>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var run = await StartPromptRunAsync(
            terminal, CreatePromptRequest(terminal, signal, reports, WorkflowPhase.Review), CodexEvidenceRules, cts.Token);
        await Task.Delay(300, cts.Token);
        Assert.Empty(terminal.Pasted);

        // Act - the footer clears; only now may the prompt go in.
        terminal.CurrentSnapshot = CodexIdleScreen;
        terminal.EmitOutput();
        await WaitUntil(() => terminal.CarriageReturnCount >= 1, cts.Token);
        WriteArtifacts(_paths.ReviewAbsolute);
        await WaitUntil(() => terminal.StartedSessions.Count >= 2, cts.Token);

        // Assert
        Assert.Single(terminal.Pasted);
        Assert.Equal(1, CarriageReturnsInFirstSession(terminal));
        Assert.False(HasUnconfirmedReport(reports));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SendPrompt_RetryUntilEvidence_FooterNeverClearsAndNoChip_SendsOneAndWarns()
    {
        // Arrange - the startup footer never clears and the paste shows no chip: neither the footer
        // nor the chip can ever prove a submit, so only today's single Enter is written.
        var terminal = new FakeTerminalController { CurrentSnapshot = CodexStartupScreen };
        terminal.OnPaste = terminal.EmitOutput;
        var signal = new ManualPhaseSignal();
        var reports = new List<string?>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // Act
        var run = await StartPromptRunAsync(
            terminal, CreatePromptRequest(terminal, signal, reports, WorkflowPhase.Review), CodexEvidenceRules, cts.Token);
        await WaitUntil(() => HasUnconfirmedReport(reports), cts.Token);

        // Assert
        Assert.Single(terminal.Pasted);
        Assert.Equal(1, CarriageReturnsInFirstSession(terminal));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SendPrompt_RetryUntilEvidence_ManualSignalDuringRetries_StopsWriting()
    {
        // Arrange - the user completes the phase while the first Enter is being verified.
        var terminal = new FakeTerminalController { CurrentSnapshot = CodexIdleScreen };
        var signal = new ManualPhaseSignal();
        terminal.OnPaste = () =>
        {
            terminal.CurrentSnapshot = CodexChipScreen;
            terminal.EmitOutput();
        };
        terminal.OnCarriageReturn = () =>
        {
            if (terminal.CarriageReturnCount == 1)
            {
                signal.Signal();
            }

            terminal.EmitOutput();
        };
        var reports = new List<string?>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var run = await StartPromptRunAsync(
            terminal, CreatePromptRequest(terminal, signal, reports, WorkflowPhase.Review), CodexEvidenceRules, cts.Token);

        // Act
        await WaitUntil(() => terminal.StartedSessions.Count >= 2, cts.Token);

        // Assert - no retry and no warning about a submit the user has just overruled.
        Assert.Equal(1, CarriageReturnsInFirstSession(terminal));
        Assert.False(HasUnconfirmedReport(reports));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SendPrompt_RetryUntilEvidence_PasteNeverLands_SendsNoCarriageReturnAndWarns()
    {
        // Arrange - nothing reacts to the paste: no chip and no output.
        var terminal = new FakeTerminalController { CurrentSnapshot = CodexIdleScreen };
        var signal = new ManualPhaseSignal();
        var reports = new List<string?>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // Act
        var run = await StartPromptRunAsync(
            terminal, CreatePromptRequest(terminal, signal, reports, WorkflowPhase.Review), CodexEvidenceRules, cts.Token);
        await WaitUntil(() => HasUnconfirmedReport(reports), cts.Token);

        // Assert
        Assert.Single(terminal.Pasted);
        Assert.Equal(0, CarriageReturnsInFirstSession(terminal));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Theory]
    [InlineData("blank")]
    [InlineData("marker-only")]
    [InlineData("scrollback")]
    public async Task SendPrompt_RetryUntilEvidence_ChipGoneWithoutCodexMarker_IsNeverEvidence(string frame)
    {
        // Arrange - every Enter takes the chip off a frame that shows no Codex marker at all: a
        // failed snapshot (""), a page holding nothing but the run marker, or plain scrollback.
        // A snapshot is lossy, so the chip's absence from such a frame proves nothing.
        var terminal = new FakeTerminalController { CurrentSnapshot = CodexIdleScreen };
        terminal.OnPaste = () =>
        {
            terminal.CurrentSnapshot = CodexChipScreen;
            terminal.EmitOutput();
        };
        terminal.OnCarriageReturn = () =>
        {
            terminal.SuppressMarkers = frame is "blank";
            terminal.CurrentSnapshot = frame is "scrollback" ? "Reviewing the plan\nsee section 4" : string.Empty;
            terminal.EmitOutput();
        };
        var signal = new ManualPhaseSignal();
        var reports = new List<string?>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // Act
        var run = await StartPromptRunAsync(
            terminal, CreatePromptRequest(terminal, signal, reports, WorkflowPhase.Review), CodexEvidenceRules, cts.Token);
        await WaitUntil(() => HasUnconfirmedReport(reports), cts.Token);

        // Assert - every attempt was used, and the user was told.
        Assert.Equal(4, CarriageReturnsInFirstSession(terminal));
        Assert.True(HasUnconfirmedReport(reports));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SendPrompt_RetryUntilEvidence_StartupFooterReturnsWithPasteAndNoChip_SendsOneAndWarns()
    {
        // Arrange - Codex looked idle, but the startup footer is back once the paste lands. A footer
        // already on screen before the Enter can never be attributed to it.
        var terminal = new FakeTerminalController { CurrentSnapshot = CodexIdleScreen };
        terminal.OnPaste = () =>
        {
            terminal.CurrentSnapshot = CodexStartupTextScreen;
            terminal.EmitOutput();
        };
        terminal.OnCarriageReturn = terminal.EmitOutput;
        var signal = new ManualPhaseSignal();
        var reports = new List<string?>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // Act
        var run = await StartPromptRunAsync(
            terminal, CreatePromptRequest(terminal, signal, reports, WorkflowPhase.Review), CodexEvidenceRules, cts.Token);
        await WaitUntil(() => HasUnconfirmedReport(reports), cts.Token);

        // Assert
        Assert.Equal(1, CarriageReturnsInFirstSession(terminal));
        Assert.True(HasUnconfirmedReport(reports));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Theory]
    [InlineData("blank")]
    [InlineData("dialog")]
    [InlineData("exit")]
    public async Task SendPrompt_RetryUntilEvidence_NoCodexScreenBeforePaste_PastesNothingAndWarns(string frame)
    {
        // Arrange - the startup footer gives way to something that is not Codex's composer: a blank
        // page, a dialog without any ready marker, or the shell after the launcher exited (its idle
        // footer still on screen above the return marker).
        var terminal = new FakeTerminalController { CurrentSnapshot = CodexStartupScreen };
        var signal = new ManualPhaseSignal();
        var reports = new List<string?>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var run = await StartPromptRunAsync(
            terminal, CreatePromptRequest(terminal, signal, reports, WorkflowPhase.Review), CodexEvidenceRules, cts.Token);

        // Long enough for the settle loop to have accepted the startup screen; well inside the
        // 3000 ms idle-wait ceiling.
        await Task.Delay(600, cts.Token);

        // Act
        switch (frame)
        {
            case "blank":
                terminal.CurrentSnapshot = string.Empty;
                terminal.EmitOutput();
                break;

            case "dialog":
                terminal.CurrentSnapshot = "Allow command?\n1. Yes\n2. No";
                terminal.EmitOutput();
                break;

            default:
                terminal.CurrentSnapshot = CodexIdleScreen;
                terminal.SimulateLauncherExit();
                break;
        }

        await WaitUntil(() => HasUnconfirmedReport(reports), cts.Token);

        // Assert - fail closed: nothing is pasted into a screen that is not Codex's.
        Assert.Empty(terminal.Pasted);
        Assert.Equal(0, CarriageReturnsInFirstSession(terminal));
        Assert.True(HasUnconfirmedReport(reports));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SendPrompt_RetryUntilEvidence_RulesWithoutSubmittedPattern_FallsBackToTheOutputCheck()
    {
        // Arrange - an older rules file without a submitted pattern: nothing in it can confirm a
        // Codex submit, so the legacy loop runs and the user is always told.
        var rules = CodexEvidenceRules.Replace(
            "\"submittedPattern\": \"(?is)esc to interrupt\",", string.Empty, StringComparison.Ordinal);
        var terminal = new FakeTerminalController { EmitOutputOnNextCarriageReturn = true, CurrentSnapshot = CodexIdleScreen };
        terminal.OnPaste = terminal.EmitOutput;
        var signal = new ManualPhaseSignal();
        var reports = new List<string?>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // Act
        var run = await StartPromptRunAsync(
            terminal, CreatePromptRequest(terminal, signal, reports, WorkflowPhase.Review), rules, cts.Token);
        await WaitUntil(() => HasUnconfirmedReport(reports), cts.Token);

        // Assert - the legacy loop stops on the CR that produced output; the fallback never reads as
        // a confirmed submit.
        Assert.Equal(1, CarriageReturnsInFirstSession(terminal));
        Assert.True(HasUnconfirmedReport(reports));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Theory]
    [InlineData("artifact")]
    [InlineData("manual")]
    [InlineData("cancellation")]
    public async Task RunSession_UnconfirmedSubmissionThenSessionEnds_ClearsTheStatus(string exit)
    {
        // Arrange - an unconfirmed submission puts the warning on the tab.
        var terminal = new FakeTerminalController();
        var signal = new ManualPhaseSignal();
        var reports = new List<string?>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var run = await StartPromptRunAsync(
            terminal, CreatePromptRequest(terminal, signal, reports), SubmitEvidenceRules, cts.Token);
        await WaitUntil(() => HasUnconfirmedReport(reports), cts.Token);

        // Act
        switch (exit)
        {
            case "artifact":
                WriteArtifacts(_paths.SpecAbsolute, _paths.PlanAbsolute);
                await WaitUntil(() => terminal.StartedSessions.Count >= 2, cts.Token);
                break;

            case "manual":
                signal.Signal();
                await WaitUntil(() => terminal.StartedSessions.Count >= 2, cts.Token);
                break;

            default:
                await cts.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
                break;
        }

        // Assert - the warning belongs to its session and must not outlive it.
        lock (reports)
        {
            Assert.Null(reports[^1]);
        }

        if (!cts.IsCancellationRequested)
        {
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
    }

    [Fact]
    public async Task SendPrompt_DecompositionSession_UsesPositiveEvidence()
    {
        // Arrange - phase 4 in subtask mode with no index yet, so the decomposition session runs.
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();
        var terminal = new FakeTerminalController();
        var signal = new ManualPhaseSignal();
        var reports = new List<string?>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var request = CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory) with
        {
            LauncherStatus = RecordInto(reports),
        };

        // Act - the paste never lands.
        var run = await StartPromptRunAsync(terminal, request, SubmitEvidenceRules, cts.Token);
        await WaitUntil(() => HasUnconfirmedReport(reports), cts.Token);

        // Assert
        Assert.Contains("ZERLEGUNG", terminal.Pasted[0], StringComparison.Ordinal);
        Assert.Equal(0, terminal.CarriageReturnCount);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SendPrompt_SubtaskRunSession_UsesPositiveEvidence()
    {
        // Arrange - an existing index, so the first session is the subtask itself: the shape of the
        // reported Workflow_LoadFix ST-001 failure.
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();
        WriteSubtaskRunPrompt();
        WriteIndex(tracking, "ST-001-alpha");
        WriteDescription(tracking, "ST-001-alpha", "Beschreibung ALPHA-ONLY");
        var terminal = new FakeTerminalController();
        var signal = new ManualPhaseSignal();
        var reports = new List<string?>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var request = CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory) with
        {
            LauncherStatus = RecordInto(reports),
        };

        // Act - the paste never lands.
        var run = await StartPromptRunAsync(terminal, request, SubmitEvidenceRules, cts.Token);
        await WaitUntil(() => HasUnconfirmedReport(reports), cts.Token);

        // Assert
        Assert.Contains("AUSFUEHRUNG <ST-001-alpha>", terminal.Pasted[0], StringComparison.Ordinal);
        Assert.Equal(0, terminal.CarriageReturnCount);

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
            Subtasks = Captured(new SubtaskConfiguration(true, trackingDirectory)),
            SubtaskProgress = new InlineProgress<SubtaskProgress>(RecordSubtaskReport),
        };

    /// <summary>
    /// A deferred capture that is already taken. For a test that fixes the configuration up front,
    /// the deferral is not the subject, so the <see cref="Lazy{T}"/> value constructor states the
    /// pair directly instead of routing through a fake source.
    /// </summary>
    private static Lazy<SubtaskConfiguration> Captured(SubtaskConfiguration configuration) =>
        new(configuration);

    /// <summary>
    /// A stand-in for the tab's live, editable configuration (task 2.5). Reads, writes and the
    /// counters all go through one lock, so a test that edits it from the test thread while the
    /// run reads it from the orchestrator's thread asserts on the design rather than on the
    /// memory model.
    /// </summary>
    private sealed class LiveConfiguration : ISubtaskConfiguration
    {
        private readonly object _gate = new();
        private bool _enabled;
        private string? _directory;
        private int _enabledReads;
        private int _directoryReads;

        public bool SubtasksEnabled
        {
            get
            {
                lock (_gate)
                {
                    _enabledReads++;
                    return _enabled;
                }
            }
        }

        public string? WorkflowDirectory
        {
            get
            {
                lock (_gate)
                {
                    _directoryReads++;
                    return _directory;
                }
            }
        }

        public int EnabledReads
        {
            get
            {
                lock (_gate)
                {
                    return _enabledReads;
                }
            }
        }

        public int DirectoryReads
        {
            get
            {
                lock (_gate)
                {
                    return _directoryReads;
                }
            }
        }

        /// <summary>Ticks the checkbox and picks the directory, as the user would mid-run.</summary>
        public void SwitchOn(string directory)
        {
            lock (_gate)
            {
                _enabled = true;
                _directory = directory;
            }
        }
    }

    [Fact]
    public async Task ARunStartedAtPhaseOne_ReadsTheConfigurationAtImplementationEntryNotAtRunStart()
    {
        // Requirement 1.6's Observable, and the whole point of task 2.5: subtask mode switched ON
        // while the earlier phases are still running must reach the implementation. The run therefore reads the live
        // source once, here - after the edit - and not at run entry, where it was still off.
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();
        StageSubtask(tracking, "ST-001-alpha", "complete");

        var live = new LiveConfiguration();
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var request = CreateRequest(terminal, signal) with
        {
            Subtasks = SubtaskConfiguration.Deferred(live),
            SubtaskProgress = new InlineProgress<SubtaskProgress>(RecordSubtaskReport),
        };

        var run = CreateOrchestrator().RunAsync(request, cts.Token);

        // Phase 1 is under way and the configuration is still off - exactly the window
        // requirement 1.6 keeps open for edits. Nothing has been read yet.
        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);
        Assert.Equal(0, live.EnabledReads);
        Assert.Equal(0, live.DirectoryReads);

        live.SwitchOn(tracking.WorkflowDirectory);

        WriteArtifacts(_paths.SpecAbsolute, _paths.PlanAbsolute);
        await WaitUntil(() => terminal.StartedSessions.Count >= 2, cts.Token);
        await CompleteSimplifierAsync(terminal, cts.Token);
        await WaitUntil(() => terminal.StartedSessions.Count >= 3, cts.Token);
        WriteArtifacts(_paths.ReviewAbsolute);
        await WaitUntil(() => terminal.StartedSessions.Count >= 4, cts.Token);
        await Task.Delay(80, cts.Token);
        WriteArtifacts(_paths.SpecAbsolute, _paths.PlanAbsolute);

        // The implementation runs as a SUBTASK run: the decomposition prompt, aimed at the directory
        // the user picked after the run had already started. Captured at run entry it would have been
        // the implementation prompt instead.
        await WaitUntil(() => terminal.Pasted.Count >= 5, cts.Token);
        Assert.Contains("ZERLEGUNG", terminal.Pasted[4], StringComparison.Ordinal);
        Assert.Contains(tracking.WorkflowDirectory, terminal.Pasted[4], StringComparison.Ordinal);

        WriteIndex(tracking, "ST-001-alpha");
        await run;

        // Exactly one capture, although the run touches the member twice: once in the phase-4
        // guard and once to compose the tracking paths.
        Assert.Equal(1, live.EnabledReads);
        Assert.Equal(1, live.DirectoryReads);
        Assert.Contains(
            _state.SubtaskSettings,
            s => s.Enabled && string.Equals(s.WorkflowDirectory, tracking.WorkflowDirectory, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARunThatNeverReachesPhaseFour_NeverReadsTheConfiguration()
    {
        // The phase-4 guard short-circuits on the phase check, so a run that ends earlier never
        // forces the deferred capture. A cancelled phase-1 run must leave the tab entirely unread.
        var live = new LiveConfiguration();
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var request = CreateRequest(terminal, signal) with
        {
            Subtasks = SubtaskConfiguration.Deferred(live),
        };

        var run = CreateOrchestrator().RunAsync(request, cts.Token);

        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.False(request.Subtasks!.IsValueCreated);
        Assert.Equal(0, live.EnabledReads);
        Assert.Equal(0, live.DirectoryReads);
    }

    /// <summary>
    /// Runs on the orchestrator's own thread the instant a report is published, so a test can put
    /// the tracking repository into a chosen state at an exactly known point of the run.
    /// </summary>
    private Action<SubtaskProgress>? _afterSubtaskReport;

    /// <summary>Records one published report, on the thread that published it.</summary>
    private void RecordSubtaskReport(SubtaskProgress report)
    {
        lock (_subtaskProgress)
        {
            _subtaskProgress.Add(report);
        }

        // Outside the lock: the hook may itself do file work, and nothing it does needs the list.
        _afterSubtaskReport?.Invoke(report);
    }

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

    /// <summary>Writes a version-2 index whose entries are given as raw JSON elements.</summary>
    private static void WriteIndexEntries(SubtaskPaths tracking, params string[] entries)
    {
        Directory.CreateDirectory(tracking.TaskDirectory);
        File.WriteAllText(
            tracking.ResultAbsolute,
            $$"""{"version":2,"task":"demo","subtasks":[{{string.Join(",", entries)}}]}""");
    }

    /// <summary>A plain entry: it depends on every previous entry.</summary>
    private static string Chained(string title) => $"\"{title}\"";

    /// <summary>An entry with an explicitly empty dependency list: it runs whatever came before.</summary>
    private static string Independent(string title) => $$"""{"title":"{{title}}","dependsOn":[]}""";

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
            Subtasks = Captured(SubtaskConfiguration.Disabled),
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

        // Task 4.5 made the last entry's OUTCOME matter to this test's closing assertion. It
        // publishes its flag with no status payload beside it - Pending under the resolved
        // vocabulary - so this run still ends without every entry complete and the deferred journal
        // transition still does not fire. The fixture moved; every assertion below is unchanged.
        PublishSubtaskResult(tracking, "ST-003-gamma", status: null);

        await run;

        // Counters, not booleans: a branch that decomposed - once or twice - must not pass either.
        Assert.DoesNotContain(terminal.Pasted, text => text.Contains("ZERLEGUNG", StringComparison.Ordinal));
        Assert.Equal(2, terminal.StartedSessions.Count);
        Assert.Equal(2, terminal.Pasted.Count);

        // The reusable index is retained. This is also what fails if the stale-flag deletion is
        // moved ahead of the reuse check.
        Assert.True(File.Exists(tracking.ResultAbsolute), "The reusable index was deleted.");

        // A reused index is not being decomposed, so the indicator never claims it is.
        Assert.DoesNotContain(SubtaskReports(), r => r.Stage == SubtaskStage.Decomposing);

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
        // The launcher is wrapped so the shell reports when it starts and when it returns;
        // the bare command is no longer written on its own. Decomposition runs unattended, so its
        // launcher carries the no-human flags (B6).
        Assert.Contains(
            "try { yo --disallowedTools AskUserQuestion --append-system-prompt-file \"",
            terminal.Sent[1],
            StringComparison.Ordinal);
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
        // The launcher is wrapped so the shell reports when it starts and when it returns;
        // the bare command is no longer written on its own. A subtask session runs unattended, so
        // its launcher carries the no-human flags (B6).
        Assert.Contains(
            "try { yo --disallowedTools AskUserQuestion --append-system-prompt-file \"",
            terminal.Sent[1],
            StringComparison.Ordinal);

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
    public async Task PhaseFour_SubtaskLoop_ASecondRunRetriesTheEntryTheFirstRunFailed()
    {
        // The task's fourth observable, and the OTHER half of design decision (issue 2): "exactly
        // one fresh attempt per run". The attempted-title set that enforces the "one" is declared
        // local to RunSubtaskLoopAsync precisely so the next Continue starts with an empty one -
        // and the application keeps a single WorkflowOrchestrator for the whole process, so the
        // two runs below deliberately share one instance. Nothing else in this class runs the same
        // orchestrator twice, so an `attempted` set promoted to a field survives every other test
        // here while silently making a failed subtask unretryable for the rest of the session.
        //
        // Asymmetric in every dimension the loop evaluates (note 1.2): two distinct titles, two
        // distinct descriptions, two different outcomes in run one, and three sessions that are
        // pairwise distinguishable by which title they carry. Beta is independent, so alpha's
        // failure does not hold it back.
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();
        WriteSubtaskRunPrompt();

        WriteIndexEntries(tracking, Chained("ST-001-alpha"), Independent("ST-002-beta"));
        WriteDescription(tracking, "ST-001-alpha", "Beschreibung ALPHA-ONLY");
        WriteDescription(tracking, "ST-002-beta", "Beschreibung BETA-ONLY");

        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // One orchestrator, two runs - the shape the application actually has.
        var orchestrator = CreateSettlingOrchestrator();

        var first = orchestrator.RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory), cts.Token);

        await WaitForPasteAsync(terminal, 0, cts.Token);
        Assert.Contains("AUSFUEHRUNG <ST-001-alpha>", terminal.Pasted[0], StringComparison.Ordinal);
        PublishSubtaskResult(tracking, "ST-001-alpha", "failed");

        await WaitForPasteAsync(terminal, 1, cts.Token);
        Assert.Contains("AUSFUEHRUNG <ST-002-beta>", terminal.Pasted[1], StringComparison.Ordinal);
        PublishSubtaskResult(tracking, "ST-002-beta", "complete");

        await first;

        // Run one really did leave exactly the state the retry is about: one failed entry, one
        // complete entry, and an implementation phase still open for recovery.
        Assert.Equal(2, terminal.StartedSessions.Count);
        Assert.DoesNotContain((WorkflowPhase.Implementation, PhaseStatus.Completed), _state.Recorded);

        var second = orchestrator.RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory), cts.Token);

        // Either the retry session pastes, or the second run ends without one. Waiting on both
        // keeps the failure a fast assertion below rather than a twenty-second timeout.
        await WaitUntil(() => terminal.Pasted.Count > 2 || second.IsCompleted, cts.Token);

        Assert.Equal(3, terminal.Pasted.Count);
        Assert.Contains("AUSFUEHRUNG <ST-001-alpha>", terminal.Pasted[2], StringComparison.Ordinal);
        Assert.Contains("Beschreibung ALPHA-ONLY", terminal.Pasted[2], StringComparison.Ordinal);

        PublishSubtaskResult(tracking, "ST-001-alpha", "complete");
        await second;

        // The retry is one session for the one still-incomplete entry: the complete one is not
        // re-run, so the second run adds exactly one session and no more.
        Assert.Equal(3, terminal.StartedSessions.Count);
        Assert.DoesNotContain(
            terminal.Pasted.Skip(2),
            text => text.Contains("ST-002-beta", StringComparison.Ordinal));

        // And the retry is what closes the task: every entry is complete on the second run's own
        // final read, so implementation is recorded Completed exactly once, by that run.
        Assert.Equal(
            new[]
            {
                (WorkflowPhase.Implementation, PhaseStatus.Active),
                (WorkflowPhase.Implementation, PhaseStatus.Active),
                (WorkflowPhase.Implementation, PhaseStatus.Completed),
            },
            _state.Recorded);
    }

    [Fact]
    public async Task PhaseFour_SubtaskLoop_TwoFailuresInARowEndTheRunBeforeTheThirdEntry()
    {
        // Circuit breaker: ST-010/011/012 in the field all died on the same foreign file, and every
        // extra session was wasted. Two consecutive failures end the run; the third entry is never
        // started, and its description never reaches a terminal. Every entry is independent, so the
        // breaker - not a dependency - is what stops the third.
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();
        WriteSubtaskRunPrompt();

        WriteIndexEntries(
            tracking, Independent("ST-001-alpha"), Independent("ST-002-beta"), Independent("ST-003-gamma"));
        WriteDescription(tracking, "ST-001-alpha", "Beschreibung ALPHA-ONLY");
        WriteDescription(tracking, "ST-002-beta", "Beschreibung BETA-ONLY");
        WriteDescription(tracking, "ST-003-gamma", "Beschreibung GAMMA-ONLY");

        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var run = CreateSettlingOrchestrator().RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory), cts.Token);

        await WaitForPasteAsync(terminal, 0, cts.Token);
        PublishSubtaskResult(tracking, "ST-001-alpha", "failed");

        await WaitForPasteAsync(terminal, 1, cts.Token);
        PublishSubtaskResult(tracking, "ST-002-beta", "failed");

        await run;

        Assert.Equal(2, terminal.StartedSessions.Count);
        Assert.DoesNotContain(terminal.Pasted, text => text.Contains("ST-003-gamma", StringComparison.Ordinal));
        Assert.DoesNotContain((WorkflowPhase.Implementation, PhaseStatus.Completed), _state.Recorded);
    }

    [Fact]
    public async Task PhaseFour_SubtaskLoop_ACompletionBetweenFailuresKeepsTheRunGoing()
    {
        // The breaker counts failures IN A ROW: fail, complete, fail must not trip it, and a single
        // failure must not stop an independent entry that follows it.
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();
        WriteSubtaskRunPrompt();

        WriteIndexEntries(
            tracking,
            Independent("ST-001-alpha"),
            Independent("ST-002-beta"),
            Independent("ST-003-gamma"),
            Independent("ST-004-delta"));
        WriteDescription(tracking, "ST-001-alpha", "Beschreibung ALPHA-ONLY");
        WriteDescription(tracking, "ST-002-beta", "Beschreibung BETA-ONLY");
        WriteDescription(tracking, "ST-003-gamma", "Beschreibung GAMMA-ONLY");
        WriteDescription(tracking, "ST-004-delta", "Beschreibung DELTA-ONLY");

        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var run = CreateSettlingOrchestrator().RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory), cts.Token);

        await WaitForPasteAsync(terminal, 0, cts.Token);
        PublishSubtaskResult(tracking, "ST-001-alpha", "failed");

        await WaitForPasteAsync(terminal, 1, cts.Token);
        PublishSubtaskResult(tracking, "ST-002-beta", "complete");

        await WaitForPasteAsync(terminal, 2, cts.Token);
        PublishSubtaskResult(tracking, "ST-003-gamma", "failed");

        await WaitForPasteAsync(terminal, 3, cts.Token);
        Assert.Contains("AUSFUEHRUNG <ST-004-delta>", terminal.Pasted[3], StringComparison.Ordinal);
        PublishSubtaskResult(tracking, "ST-004-delta", "complete");

        await run;

        Assert.Equal(4, terminal.StartedSessions.Count);
    }

    [Fact]
    public async Task PhaseFour_SubtaskLoop_ANewRunAfterTheBreakerFiredRetriesTheFailedEntries()
    {
        // Continue stays the user's explicit retry: the breaker is local to one run.
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();
        WriteSubtaskRunPrompt();

        WriteIndexEntries(
            tracking, Chained("ST-001-alpha"), Independent("ST-002-beta"), Chained("ST-003-gamma"));
        WriteDescription(tracking, "ST-001-alpha", "Beschreibung ALPHA-ONLY");
        WriteDescription(tracking, "ST-002-beta", "Beschreibung BETA-ONLY");
        WriteDescription(tracking, "ST-003-gamma", "Beschreibung GAMMA-ONLY");

        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var orchestrator = CreateSettlingOrchestrator();

        var first = orchestrator.RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory), cts.Token);

        await WaitForPasteAsync(terminal, 0, cts.Token);
        PublishSubtaskResult(tracking, "ST-001-alpha", "failed");
        await WaitForPasteAsync(terminal, 1, cts.Token);
        PublishSubtaskResult(tracking, "ST-002-beta", "failed");
        await first;

        var second = orchestrator.RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory), cts.Token);

        await WaitUntil(() => terminal.Pasted.Count > 2 || second.IsCompleted, cts.Token);

        Assert.Equal(3, terminal.Pasted.Count);
        Assert.Contains("AUSFUEHRUNG <ST-001-alpha>", terminal.Pasted[2], StringComparison.Ordinal);

        PublishSubtaskResult(tracking, "ST-001-alpha", "complete");
        await WaitForPasteAsync(terminal, 3, cts.Token);
        Assert.Contains("AUSFUEHRUNG <ST-002-beta>", terminal.Pasted[3], StringComparison.Ordinal);

        PublishSubtaskResult(tracking, "ST-002-beta", "complete");
        await WaitForPasteAsync(terminal, 4, cts.Token);
        PublishSubtaskResult(tracking, "ST-003-gamma", "complete");
        await second;
    }

    [Fact]
    public async Task PhaseFour_SubtaskLoop_SkipsUnsafeTitlesAndMissingDescriptions()
    {
        // Requirement 2.10: an unsafe title and an entry without a usable description each fail
        // only themselves, without a session and without composing a path outside the tracking
        // repository. The fixture is asymmetric: an unsafe title, a safe title whose subtask.md is
        // absent, a safe title whose subtask.md is blank, and one attemptable entry - last, so a
        // loop that stopped at the first bad entry never reaches it. All but the first are
        // independent, so each bad entry is reached through the skip path, not a dependency.
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();
        WriteSubtaskRunPrompt();

        WriteIndexEntries(
            tracking,
            Chained(".."),
            Independent("ST-002-beta"),
            Independent("ST-003-gamma"),
            Independent("ST-004-delta"));
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

        // Independent, so the still-unfinished sibling does not hold it back and the guard is what
        // has to stop it.
        WriteIndexEntries(tracking, Chained("ST-001-alpha"), Independent("ST-001-alpha."));
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

    [Fact]
    public async Task PhaseFour_SubtaskLoop_AWaitingEntryHoldsBackItsDependentsButNotIndependentWork()
    {
        // The problem this pins: a subtask waiting for a human used to cost every later subtask a
        // full session that only wrote "blocked". Now beta, which depends on everything before it,
        // is never launched; gamma and delta, both independent, are. Gamma fails and delta still
        // runs, which is only possible if the waiting alpha did not count towards the breaker.
        const string Reason = "Freigabe durch den Fachbereich";

        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();
        WriteSubtaskRunPrompt();

        WriteIndexEntries(
            tracking,
            Chained("ST-001-alpha"),
            Chained("ST-002-beta"),
            Independent("ST-003-gamma"),
            Independent("ST-004-delta"));
        WriteDescription(tracking, "ST-001-alpha", "Beschreibung ALPHA-ONLY");
        WriteDescription(tracking, "ST-002-beta", "Beschreibung BETA-ONLY");
        WriteDescription(tracking, "ST-003-gamma", "Beschreibung GAMMA-ONLY");
        WriteDescription(tracking, "ST-004-delta", "Beschreibung DELTA-ONLY");

        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var run = CreateSettlingOrchestrator().RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory), cts.Token);

        await WaitForPasteAsync(terminal, 0, cts.Token);
        Assert.Contains("AUSFUEHRUNG <ST-001-alpha>", terminal.Pasted[0], StringComparison.Ordinal);
        PublishRawStatusAndFlag(
            tracking, "ST-001-alpha", $$"""{"status":"waiting_for_human","failreason":"{{Reason}}"}""");

        await WaitForPasteAsync(terminal, 1, cts.Token);
        Assert.Contains("AUSFUEHRUNG <ST-003-gamma>", terminal.Pasted[1], StringComparison.Ordinal);
        PublishSubtaskResult(tracking, "ST-003-gamma", "failed");

        await WaitForPasteAsync(terminal, 2, cts.Token);
        Assert.Contains("AUSFUEHRUNG <ST-004-delta>", terminal.Pasted[2], StringComparison.Ordinal);
        PublishSubtaskResult(tracking, "ST-004-delta", "complete");

        await run;

        Assert.Equal(3, terminal.StartedSessions.Count);
        Assert.DoesNotContain(terminal.Pasted, text => text.Contains("ST-002-beta", StringComparison.Ordinal));
        Assert.False(File.Exists(tracking.SubtaskResultFile("ST-002-beta")));

        // The run ends cleanly with the reason it stopped, and stays recoverable.
        var final = SubtaskReports()[^1];
        Assert.Equal(SubtaskStage.Finished, final.Stage);
        Assert.Null(final.CurrentTitle);
        Assert.Equal(1, final.Waiting);
        Assert.Equal(1, final.Failed);
        Assert.Equal(1, final.Completed);
        Assert.NotNull(final.BlockedReason);
        Assert.Contains(
            $"ST-002-beta wartet auf ST-001-alpha (waiting_for_human: {Reason})",
            final.BlockedReason,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            _state.Recorded, r => r.Phase == WorkflowPhase.Implementation && r.Status == PhaseStatus.Completed);
    }

    [Fact]
    public async Task PhaseFour_SubtaskLoop_ADeferredEntryEndsTheRunWaitingForItsDesktopJob()
    {
        // A deferred entry is never handed to a session; with nothing else runnable the run ends
        // with the distinct desktop-job message instead of starting one.
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();
        WriteSubtaskRunPrompt();

        WriteIndex(tracking, "ST-001-alpha", "ST-002-beta");
        WriteDescription(tracking, "ST-001-alpha", "Beschreibung ALPHA-ONLY");
        WriteDescription(tracking, "ST-002-beta", "Beschreibung BETA-ONLY");
        WriteStatus(tracking, "ST-001-alpha", """{"status":"deferred"}""");

        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await CreateSettlingOrchestrator().RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory), cts.Token);

        Assert.Empty(terminal.StartedSessions);

        var final = SubtaskReports()[^1];
        Assert.Equal(SubtaskStage.Finished, final.Stage);
        Assert.Equal(1, final.Deferred);
        Assert.NotNull(final.BlockedReason);
        Assert.Contains("Desktop-Job wartet", final.BlockedReason, StringComparison.Ordinal);
        Assert.Contains("ST-001-alpha", final.BlockedReason, StringComparison.Ordinal);
    }

    // --- Phase 4, desktop jobs and the execution gate ----------------------------------------

    /// <summary>Stages ST-001-alpha deferred with a one-step desktop job; ST-002-beta depends on it.</summary>
    private SubtaskPaths StageDeferredDesktopJob(int timeoutMinutes = 45)
    {
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();
        WriteSubtaskRunPrompt();

        WriteIndex(tracking, "ST-001-alpha", "ST-002-beta");
        WriteDescription(tracking, "ST-001-alpha", "Beschreibung ALPHA-ONLY");
        WriteDescription(tracking, "ST-002-beta", "Beschreibung BETA-ONLY");
        WriteStatus(tracking, "ST-001-alpha", """{"status":"deferred","failreason":"Desktop-Job"}""");

        WriteDesktopJobRequest(tracking, "ST-001-alpha", timeoutMinutes);

        return tracking;
    }

    private WorkflowOrchestrator CreateDesktopJobOrchestrator(
        DesktopJobRunner runner,
        IExecutionGate? gate = null)
    {
        var path = Path.Combine(_root, "rules-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, ShortAutoAnswerCeilingRules);

        return new WorkflowOrchestrator(
            new PromptTemplateService(_promptDir),
            new AutoAnswerService(path, overridePath: null),
            new ArtifactWatcherFactory(),
            _state,
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(40),
            gate: gate,
            desktopJobs: runner,
            requireDecisionApproval: false);
    }

    [Fact]
    public async Task PhaseFour_DesktopJob_PollsUntilIdleThenRunsTheJobAndTheVerificationSessionInTheSameRun()
    {
        var tracking = StageDeferredDesktopJob();

        var monitor = new FakeUserActivityMonitor { Activity = new UserActivity(TimeSpan.Zero, false, false) };
        var starter = new FakeDesktopJobProcessStarter { MarkerFor = _ => Path.Combine(_root, "serie.marker") };
        var clock = new FakeDesktopJobClock();
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();

        // The user comes back to nothing for three polls, then leaves for good.
        var sessionsWhilePolling = new List<int>();
        clock.OnDelay = ordinal =>
        {
            sessionsWhilePolling.Add(terminal.StartedSessions.Count);
            if (ordinal == 3)
            {
                monitor.Activity = new UserActivity(TimeSpan.FromHours(1), false, false);
            }
        };

        var runner = new DesktopJobRunner(monitor, new FakeCpuLoadProbe(), starter, clock);
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var run = CreateDesktopJobOrchestrator(runner).RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory), cts.Token);

        // The verification session for the deferred entry, launched once the job delivered.
        await WaitForPasteAsync(terminal, 0, cts.Token);
        Assert.Contains("AUSFUEHRUNG <ST-001-alpha>", terminal.Pasted[0], StringComparison.Ordinal);
        Assert.Single(starter.Launches);
        Assert.Equal([0, 0, 0], sessionsWhilePolling.Take(3));
        Assert.Equal(
            [TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)],
            clock.Delays.Take(3));
        PublishSubtaskResult(tracking, "ST-001-alpha", "complete");

        // Only now does the dependent run.
        await WaitForPasteAsync(terminal, 1, cts.Token);
        Assert.Contains("AUSFUEHRUNG <ST-002-beta>", terminal.Pasted[1], StringComparison.Ordinal);
        PublishSubtaskResult(tracking, "ST-002-beta", "complete");

        await run;

        Assert.Equal(2, terminal.StartedSessions.Count);
        Assert.Contains(
            _state.Recorded,
            r => r.Phase == WorkflowPhase.Implementation && r.Status == PhaseStatus.Completed);
    }

    // --- WF_DESKTOPJOBBUTTON: "Desktop-Job jetzt starten" ------------------------------------
    // In every one of these the user has been idle for one minute only: the request's 15-minute
    // window never opens on its own, so only a press can start the job.

    private static UserActivity IdleForOneMinute() => new(TimeSpan.FromMinutes(1), false, false);

    /// <summary>
    /// A <see cref="FakeDesktopJobClock"/> whose poll number <paramref name="pressOrdinal"/> is the one the user
    /// presses during: it runs <paramref name="press"/> and then never completes on its own, so that poll can end
    /// only through the press racing it. Every other delay - the session stall polls after the job included -
    /// behaves exactly as the inner clock's.
    /// </summary>
    private sealed class PressDuringPollClock(FakeDesktopJobClock inner, int pressOrdinal, Action press) : IDesktopJobClock
    {
        private int _delays;

        public DateTimeOffset UtcNow => inner.UtcNow;

        public async Task DelayAsync(TimeSpan interval, CancellationToken cancellationToken)
        {
            await inner.DelayAsync(interval, cancellationToken);

            if (Interlocked.Increment(ref _delays) == pressOrdinal)
            {
                press();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
        }
    }

    [Fact]
    public async Task PhaseFour_DesktopJob_StartNowDuringTheIdleWaitRunsTheJobAtOnceAndThenTheVerification()
    {
        // Arrange
        var tracking = StageDeferredDesktopJob();
        var monitor = new FakeUserActivityMonitor { Activity = IdleForOneMinute() };
        var starter = new FakeDesktopJobProcessStarter { MarkerFor = _ => Path.Combine(_root, "serie.marker") };
        var cpu = new FakeCpuLoadProbe();
        var clock = new FakeDesktopJobClock();
        var start = new DesktopJobStartSignal();
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();

        // The same fake clock serves the runner's watch and any session stall poll after the job,
        // so only the first two delays - the two idle polls - are looked at.
        var waitingAtPoll = new List<bool>();
        clock.OnDelay = ordinal =>
        {
            if (ordinal <= 2)
            {
                waitingAtPoll.Add(start.IsWaiting);
            }
        };

        // Pressed during the second poll, which then never ends by itself: the job runs at once only
        // because the press races the poll.
        var runner = new DesktopJobRunner(monitor, cpu, starter, new PressDuringPollClock(clock, 2, start.Request));
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        var run = CreateDesktopJobOrchestrator(runner).RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory) with { DesktopJobStart = start },
            cts.Token);

        await WaitForPasteAsync(terminal, 0, cts.Token);
        Assert.Contains("AUSFUEHRUNG <ST-001-alpha>", terminal.Pasted[0], StringComparison.Ordinal);
        PublishSubtaskResult(tracking, "ST-001-alpha", "complete");

        await WaitForPasteAsync(terminal, 1, cts.Token);
        Assert.Contains("AUSFUEHRUNG <ST-002-beta>", terminal.Pasted[1], StringComparison.Ordinal);
        PublishSubtaskResult(tracking, "ST-002-beta", "complete");

        await run;

        // Assert - the idle window never opened, so only the press can have launched the job, unmeasured.
        Assert.Single(starter.Launches);
        Assert.Empty(cpu.Windows);
        Assert.Equal([true, true], waitingAtPoll);
        Assert.False(start.IsWaiting);
    }

    [Fact]
    public async Task PhaseFour_DesktopJob_APressBeforeTheWaitDoesNotSkipTheIdleWindow()
    {
        // Arrange
        var tracking = StageDeferredDesktopJob();
        var monitor = new FakeUserActivityMonitor { Activity = IdleForOneMinute() };
        var starter = new FakeDesktopJobProcessStarter { MarkerFor = _ => Path.Combine(_root, "serie.marker") };
        var cpu = new FakeCpuLoadProbe();
        var clock = new FakeDesktopJobClock();
        var start = new DesktopJobStartSignal();
        var signal = new ManualPhaseSignal();
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();

        clock.OnDelay = ordinal =>
        {
            if (ordinal == 3)
            {
                signal.Signal();
            }
        };

        var runner = new DesktopJobRunner(monitor, cpu, starter, clock);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act - pressed while nothing waits: the press must not be carried into the run's wait.
        start.Request();
        await CreateDesktopJobOrchestrator(runner).RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory) with { DesktopJobStart = start },
            cts.Token);

        // Assert
        Assert.Empty(starter.Launches);
        Assert.Empty(cpu.Windows);
        Assert.False(start.IsWaiting);
    }

    [Fact]
    public async Task PhaseFour_DesktopJob_AStartNowOnALockedDesktopLeavesTheNextWaitNormal()
    {
        // Arrange - locked throughout: the press is honoured, but the runner refuses the locked desktop.
        var tracking = StageDeferredDesktopJob();
        var monitor = new FakeUserActivityMonitor { Activity = new UserActivity(TimeSpan.FromMinutes(1), true, false) };
        var starter = new FakeDesktopJobProcessStarter { MarkerFor = _ => Path.Combine(_root, "serie.marker") };
        var cpu = new FakeCpuLoadProbe();
        var clock = new FakeDesktopJobClock();
        var start = new DesktopJobStartSignal();
        var signal = new ManualPhaseSignal();
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();

        // Delays 1-2: the first wait (pressed at 2); 3-5: the second wait. Counting the waits pins both
        // halves: an ignored press keeps the first wait polling (one wait at delay 5), a press carried
        // into the next wait ends it without a poll and opens a third.
        var waitsStarted = 0;
        var waitsAtTheEnd = -1;
        start.WaitingChanged += (_, _) =>
        {
            if (start.IsWaiting)
            {
                waitsStarted++;
            }
        };

        clock.OnDelay = ordinal =>
        {
            if (ordinal == 2)
            {
                start.Request();
            }

            if (ordinal == 5)
            {
                waitsAtTheEnd = waitsStarted;
                signal.Signal();
            }
        };

        var runner = new DesktopJobRunner(monitor, cpu, starter, clock);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        await CreateDesktopJobOrchestrator(runner).RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory) with { DesktopJobStart = start },
            cts.Token);

        // Assert - nothing launched, measured or recorded; the press was used up by the refused attempt.
        Assert.Equal(2, waitsAtTheEnd);
        Assert.Empty(starter.Launches);
        Assert.Empty(cpu.Windows);
        Assert.Null(DesktopJobFiles.ReadResult(tracking.SubtaskDirectory("ST-001-alpha")));
        Assert.False(start.IsWaiting);
    }

    [Fact]
    public async Task PhaseFour_DesktopJob_TaskAbschliessenAfterTheStartNowPress_LaunchesNothing()
    {
        // Arrange
        var tracking = StageDeferredDesktopJob();
        var monitor = new FakeUserActivityMonitor { Activity = IdleForOneMinute() };
        var starter = new FakeDesktopJobProcessStarter { MarkerFor = _ => Path.Combine(_root, "serie.marker") };
        var cpu = new FakeCpuLoadProbe();
        var clock = new FakeDesktopJobClock();
        var start = new DesktopJobStartSignal();
        var signal = new ManualPhaseSignal();
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();

        var pressed = false;
        var finished = false;
        clock.OnDelay = ordinal =>
        {
            if (ordinal == 2)
            {
                pressed = true;
                start.Request();
            }
        };

        // 'Task abschliessen' while the session is being stopped: the wait loop has been left already.
        terminal.OnStopSession = () =>
        {
            if (pressed && !finished)
            {
                finished = true;
                signal.Signal();
            }
        };

        var runner = new DesktopJobRunner(monitor, cpu, starter, clock);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        await CreateDesktopJobOrchestrator(runner).RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory) with { DesktopJobStart = start },
            cts.Token);

        // Assert
        Assert.True(finished);
        Assert.Empty(starter.Launches);
        Assert.Empty(cpu.Windows);
        Assert.False(start.IsWaiting);
    }

    [Fact]
    public async Task PhaseFour_DesktopJob_PauseAfterTheStartNowPress_DropsThePressAndHoldsInTheNextWait()
    {
        // Arrange
        var tracking = StageDeferredDesktopJob();
        var monitor = new FakeUserActivityMonitor { Activity = IdleForOneMinute() };
        var starter = new FakeDesktopJobProcessStarter { MarkerFor = _ => Path.Combine(_root, "serie.marker") };
        var cpu = new FakeCpuLoadProbe();
        var clock = new FakeDesktopJobClock();
        var start = new DesktopJobStartSignal();
        var pause = new PhasePauseGate();
        var signal = new ManualPhaseSignal();
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();

        var pressed = false;
        var paused = false;
        clock.OnDelay = ordinal =>
        {
            if (ordinal == 2)
            {
                pressed = true;
                start.Request();
            }
        };

        // Pause while the session is being stopped: the wait loop has been left already.
        terminal.OnStopSession = () =>
        {
            if (pressed && !paused)
            {
                paused = true;
                pause.Pause();
            }
        };

        var waitsBegun = 0;
        var secondWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        start.WaitingChanged += (_, _) =>
        {
            if (start.IsWaiting && Interlocked.Increment(ref waitsBegun) == 2)
            {
                secondWait.TrySetResult();
            }
        };

        var runner = new DesktopJobRunner(monitor, cpu, starter, clock);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        var run = CreateDesktopJobOrchestrator(runner).RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory) with { Pause = pause, DesktopJobStart = start },
            cts.Token);

        // The run re-entered a new wait; it holds there while paused, until the task is finished.
        await secondWait.Task.WaitAsync(cts.Token);
        signal.Signal();
        await run;

        // Assert - no poll after the press: the new wait held instead of polling.
        Assert.Equal(2, Volatile.Read(ref waitsBegun));
        Assert.Equal(2, clock.Delays.Count);
        Assert.Empty(starter.Launches);
        Assert.Empty(cpu.Windows);
        Assert.False(start.IsWaiting);
    }

    [Fact]
    public async Task PhaseFour_DesktopJob_ARequestReplacedAfterTheStartNowPress_IsNotStartedByIt()
    {
        // Arrange
        var tracking = StageDeferredDesktopJob();
        var monitor = new FakeUserActivityMonitor { Activity = IdleForOneMinute() };
        var starter = new FakeDesktopJobProcessStarter { MarkerFor = _ => Path.Combine(_root, "serie.marker") };
        var cpu = new FakeCpuLoadProbe();
        var clock = new FakeDesktopJobClock();
        var start = new DesktopJobStartSignal();
        var signal = new ManualPhaseSignal();
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();

        var pressed = false;
        var replaced = false;
        var finished = false;
        clock.OnDelay = ordinal =>
        {
            if (ordinal == 2)
            {
                pressed = true;
                start.Request();
            }
            else if (replaced && !finished)
            {
                // A poll of the wait that follows: the run kept waiting for the new request's window.
                finished = true;
                signal.Signal();
            }
        };

        // desktopjob.json replaced under a new requestId while the session is being stopped.
        terminal.OnStopSession = () =>
        {
            if (pressed && !replaced)
            {
                replaced = true;
                WriteDesktopJobRequest(tracking, "ST-001-alpha");
            }
        };

        var runner = new DesktopJobRunner(monitor, cpu, starter, clock);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        await CreateDesktopJobOrchestrator(runner).RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory) with { DesktopJobStart = start },
            cts.Token);

        // Assert - the replaced job got its own idle requirement: nothing launched or recorded.
        Assert.True(finished);
        Assert.Empty(starter.Launches);
        Assert.Empty(cpu.Windows);
        Assert.Null(DesktopJobFiles.ReadResult(tracking.SubtaskDirectory("ST-001-alpha")));
        Assert.False(start.IsWaiting);
    }

    [Fact]
    public async Task PhaseFour_DesktopJob_AnExhaustedJobEndsTheRunTerminallyBlockedWithItsReason()
    {
        var tracking = StageDeferredDesktopJob();

        var monitor = new FakeUserActivityMonitor();
        var starter = new FakeDesktopJobProcessStarter { StartFailure = FakeDesktopJobProcessStarter.NotFound() };
        var runner = new DesktopJobRunner(monitor, new FakeCpuLoadProbe(), starter, new FakeDesktopJobClock());
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await CreateDesktopJobOrchestrator(runner).RunAsync(
            CreateSubtaskRequest(terminal, new ManualPhaseSignal(), tracking.WorkflowDirectory), cts.Token);

        Assert.Empty(terminal.StartedSessions);

        var final = SubtaskReports()[^1];
        Assert.Equal(SubtaskStage.Finished, final.Stage);
        Assert.Contains(
            "Desktop-Job ST-001-alpha/serie erschöpft: start_failed",
            final.BlockedReason,
            StringComparison.Ordinal);

        var recorded = Assert.Single(
            DesktopJobFiles.ReadResult(tracking.SubtaskDirectory("ST-001-alpha"))!.Steps,
            step => step.StepId == "serie");
        Assert.Equal(DesktopJobOutcome.StartFailed, recorded.Outcome);
        Assert.Equal(2, recorded.Attempts);
    }

    [Fact]
    public async Task PhaseFour_DesktopJob_AFailedScriptStartsARepairSessionAndTheRepairedJobIsVerified()
    {
        var tracking = StageDeferredDesktopJob();

        var monitor = new FakeUserActivityMonitor();
        var starter = new FakeDesktopJobProcessStarter { ExitCode = 1 };
        var runner = new DesktopJobRunner(monitor, new FakeCpuLoadProbe(), starter, new FakeDesktopJobClock());
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var run = CreateDesktopJobOrchestrator(runner).RunAsync(
            CreateSubtaskRequest(terminal, new ManualPhaseSignal(), tracking.WorkflowDirectory), cts.Token);

        // The repair session, started because the step ran and failed - not a second job run.
        await WaitForPasteAsync(terminal, 0, cts.Token);
        Assert.Contains("AUSFUEHRUNG <ST-001-alpha>", terminal.Pasted[0], StringComparison.Ordinal);
        Assert.Single(starter.Launches);

        // The agent repairs: a new request, deferred again; this time the script succeeds.
        starter.ExitCode = 0;
        starter.MarkerFor = _ => Path.Combine(_root, "serie.marker");
        WriteDesktopJobRequest(tracking, "ST-001-alpha");
        PublishSubtaskResult(tracking, "ST-001-alpha", "deferred");

        // The verification session for the repaired job.
        await WaitForPasteAsync(terminal, 1, cts.Token);
        Assert.Contains("AUSFUEHRUNG <ST-001-alpha>", terminal.Pasted[1], StringComparison.Ordinal);
        Assert.Equal(2, starter.Launches.Count);
        PublishSubtaskResult(tracking, "ST-001-alpha", "complete");

        await WaitForPasteAsync(terminal, 2, cts.Token);
        Assert.Contains("AUSFUEHRUNG <ST-002-beta>", terminal.Pasted[2], StringComparison.Ordinal);
        PublishSubtaskResult(tracking, "ST-002-beta", "complete");

        await run;
        Assert.Contains(_state.Recorded, r => r.Phase == WorkflowPhase.Implementation && r.Status == PhaseStatus.Completed);
    }

    [Fact]
    public async Task PhaseFour_DesktopJob_AFailureRecordedByAnEarlierRunStartsARepairSessionBeforeAnyJob()
    {
        var tracking = StageDeferredDesktopJob();

        // The result an earlier run left, before this run starts.
        var directory = tracking.SubtaskDirectory("ST-001-alpha");
        var request = DesktopJobFiles.ReadRequest(directory).Request;
        Assert.NotNull(request);
        var started = new DateTimeOffset(2026, 10, 20, 7, 0, 0, TimeSpan.Zero);
        DesktopJobFiles.WriteResult(
            directory,
            new DesktopJobResult(
                request.RequestId.ToString("D"),
                DesktopJobFiles.ConfigSha256(request),
                [new DesktopJobStepResult("serie", DesktopJobFiles.ScriptSha256(directory, "desktopjob.serie.ps1"), DesktopJobOutcome.Timeout, null, false, started, started.AddMinutes(45), 2)]));

        var monitor = new FakeUserActivityMonitor();
        var starter = new FakeDesktopJobProcessStarter(); // would succeed - it must not be asked
        var runner = new DesktopJobRunner(monitor, new FakeCpuLoadProbe(), starter, new FakeDesktopJobClock());
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var run = CreateDesktopJobOrchestrator(runner).RunAsync(
            CreateSubtaskRequest(terminal, new ManualPhaseSignal(), tracking.WorkflowDirectory), cts.Token);

        await WaitForPasteAsync(terminal, 0, cts.Token);
        Assert.Contains("AUSFUEHRUNG <ST-001-alpha>", terminal.Pasted[0], StringComparison.Ordinal);
        Assert.Empty(starter.Launches);

        PublishSubtaskResult(tracking, "ST-001-alpha", "complete");
        await WaitForPasteAsync(terminal, 1, cts.Token);
        PublishSubtaskResult(tracking, "ST-002-beta", "complete");
        await run;
        Assert.Empty(starter.Launches);
    }

    [Fact]
    public async Task PhaseFour_DesktopJob_RepairSessionsAreBoundedPerRun()
    {
        var tracking = StageDeferredDesktopJob();

        var monitor = new FakeUserActivityMonitor();
        var starter = new FakeDesktopJobProcessStarter { ExitCode = 1 }; // every request fails
        var runner = new DesktopJobRunner(monitor, new FakeCpuLoadProbe(), starter, new FakeDesktopJobClock());
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var run = CreateDesktopJobOrchestrator(runner).RunAsync(
            CreateSubtaskRequest(terminal, new ManualPhaseSignal(), tracking.WorkflowDirectory), cts.Token);

        for (var session = 0; session < 2; session++)
        {
            await WaitForPasteAsync(terminal, session, cts.Token);
            WriteDesktopJobRequest(tracking, "ST-001-alpha");
            PublishSubtaskResult(tracking, "ST-001-alpha", "deferred");
        }

        await run;

        Assert.Equal(3, starter.Launches.Count);
        Assert.Equal(2, terminal.StartedSessions.Count);
        Assert.Contains(
            "Desktop-Job ST-001-alpha/serie fehlgeschlagen: script_failed; keine weitere Reparatursitzung in diesem Lauf",
            SubtaskReports()[^1].BlockedReason,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PhaseFour_DesktopJob_AnAbortedAttemptBeforeAFailureStillStartsARepairSession()
    {
        var tracking = StageDeferredDesktopJob();

        // The first attempt is aborted by real input (counted), the second fails its script. Before
        // WF_REPAIR the second counted outcome exhausted the job; a repair outcome must never do so.
        var monitor = new FakeUserActivityMonitor();
        var starter = new FakeDesktopJobProcessStarter { ExitCode = null };
        starter.OnStart = _ =>
        {
            if (starter.Processes.Count == 1)
            {
                monitor.LastWatch!.ReportMouse(flags: 0);
            }
            else
            {
                starter.ExitCode = 1;
            }
        };
        var runner = new DesktopJobRunner(monitor, new FakeCpuLoadProbe(), starter, new FakeDesktopJobClock());
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var run = CreateDesktopJobOrchestrator(runner).RunAsync(
            CreateSubtaskRequest(terminal, new ManualPhaseSignal(), tracking.WorkflowDirectory), cts.Token);

        await WaitForPasteAsync(terminal, 0, cts.Token);
        Assert.Equal(2, starter.Launches.Count);

        // The repaired request must run: a stale exhaustion would keep it from ever starting.
        starter.ExitCode = 0;
        starter.MarkerFor = _ => Path.Combine(_root, "serie.marker");
        WriteDesktopJobRequest(tracking, "ST-001-alpha");
        PublishSubtaskResult(tracking, "ST-001-alpha", "deferred");

        await WaitForPasteAsync(terminal, 1, cts.Token);
        Assert.Equal(3, starter.Launches.Count);
        PublishSubtaskResult(tracking, "ST-001-alpha", "failed");
        await run;
    }

    [Fact]
    public async Task PhaseFour_DesktopJob_ARepairSessionWithoutANewRequestEndsTheRunWithItsReason()
    {
        var tracking = StageDeferredDesktopJob();

        var monitor = new FakeUserActivityMonitor();
        var starter = new FakeDesktopJobProcessStarter { ExitCode = 1 };
        var runner = new DesktopJobRunner(monitor, new FakeCpuLoadProbe(), starter, new FakeDesktopJobClock());
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var run = CreateDesktopJobOrchestrator(runner).RunAsync(
            CreateSubtaskRequest(terminal, new ManualPhaseSignal(), tracking.WorkflowDirectory), cts.Token);

        await WaitForPasteAsync(terminal, 0, cts.Token);
        PublishSubtaskResult(tracking, "ST-001-alpha", "deferred");

        await run;

        Assert.Single(starter.Launches);
        Assert.Single(terminal.StartedSessions);
        Assert.Contains(
            "deferred: Desktop-Job fehlgeschlagen, keine neue Anforderung",
            SubtaskReports()[^1].BlockedReason,
            StringComparison.Ordinal);
    }
    [Fact]
    public async Task PhaseFour_DesktopJob_AFailedRequestIsNotLaunchedAgainInTheNextRunOfTheSameOrchestrator()
    {
        // Spec AC2: a request that failed its script in one run is not launched again by the next run.
        // Both runs share one orchestrator, the shape the application has.

        // Arrange
        var tracking = StageDeferredDesktopJob();
        var monitor = new FakeUserActivityMonitor();
        var starter = new FakeDesktopJobProcessStarter { ExitCode = 1 };
        var runner = new DesktopJobRunner(monitor, new FakeCpuLoadProbe(), starter, new FakeDesktopJobClock());
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var orchestrator = CreateDesktopJobOrchestrator(runner);

        // Act - run 1: the job fails, the repair session answers without a new request.
        var first = orchestrator.RunAsync(
            CreateSubtaskRequest(terminal, new ManualPhaseSignal(), tracking.WorkflowDirectory), cts.Token);
        await WaitForPasteAsync(terminal, 0, cts.Token);
        Assert.Contains("AUSFUEHRUNG <ST-001-alpha>", terminal.Pasted[0], StringComparison.Ordinal);
        Assert.Single(starter.Launches);
        PublishSubtaskResult(tracking, "ST-001-alpha", "deferred");
        await first;
        Assert.Contains(
            "deferred: Desktop-Job fehlgeschlagen, keine neue Anforderung",
            SubtaskReports()[^1].BlockedReason,
            StringComparison.Ordinal);

        // Act - run 2: the failed request opens a repair session first, not a second launch.
        var second = orchestrator.RunAsync(
            CreateSubtaskRequest(terminal, new ManualPhaseSignal(), tracking.WorkflowDirectory), cts.Token);
        await WaitForPasteAsync(terminal, 1, cts.Token);
        Assert.Contains("AUSFUEHRUNG <ST-001-alpha>", terminal.Pasted[1], StringComparison.Ordinal);
        Assert.Single(starter.Launches); // the failed request was not launched again across the run boundary

        // The agent repairs: a new request, deferred again; this time the script succeeds.
        starter.ExitCode = 0;
        starter.MarkerFor = _ => Path.Combine(_root, "serie.marker");
        WriteDesktopJobRequest(tracking, "ST-001-alpha");
        PublishSubtaskResult(tracking, "ST-001-alpha", "deferred");
        await WaitForPasteAsync(terminal, 2, cts.Token); // the verification session
        Assert.Contains("AUSFUEHRUNG <ST-001-alpha>", terminal.Pasted[2], StringComparison.Ordinal);
        Assert.Equal(2, starter.Launches.Count);
        PublishSubtaskResult(tracking, "ST-001-alpha", "complete");
        await WaitForPasteAsync(terminal, 3, cts.Token);
        Assert.Contains("AUSFUEHRUNG <ST-002-beta>", terminal.Pasted[3], StringComparison.Ordinal);
        PublishSubtaskResult(tracking, "ST-002-beta", "complete");
        await second;

        // Assert
        Assert.Contains(_state.Recorded, r => r.Phase == WorkflowPhase.Implementation && r.Status == PhaseStatus.Completed);
    }

    [Theory]
    [InlineData(DesktopJobOutcome.AbortedBusy)]
    [InlineData(DesktopJobOutcome.AbortedUserInput)]
    public async Task PhaseFour_DesktopJob_AFailureOverwrittenByALaterAbort_GetsOneMoreAttemptThenARepairSession(string latestOutcome)
    {
        // Spec AC3 and 5.2: the earlier failure was overwritten by a later abort in the result file.

        // Arrange - the result an earlier, pre-WF_REPAIR run left behind.
        var tracking = StageDeferredDesktopJob();
        var directory = tracking.SubtaskDirectory("ST-001-alpha");
        var request = DesktopJobFiles.ReadRequest(directory).Request;
        Assert.NotNull(request);
        var started = new DateTimeOffset(2026, 10, 20, 7, 0, 0, TimeSpan.Zero);
        // why: the earlier timeout was overwritten by the abort; the file cannot show it, so the job gets one more attempt.
        DesktopJobFiles.WriteResult(
            directory,
            new DesktopJobResult(
                request.RequestId.ToString("D"),
                DesktopJobFiles.ConfigSha256(request),
                [new DesktopJobStepResult("serie", DesktopJobFiles.ScriptSha256(directory, "desktopjob.serie.ps1"), latestOutcome, null, false, started, started.AddMinutes(45), 2)]));
        var monitor = new FakeUserActivityMonitor();
        var starter = new FakeDesktopJobProcessStarter { ExitCode = 1 };
        var runner = new DesktopJobRunner(monitor, new FakeCpuLoadProbe(), starter, new FakeDesktopJobClock());
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        var run = CreateDesktopJobOrchestrator(runner).RunAsync(
            CreateSubtaskRequest(terminal, new ManualPhaseSignal(), tracking.WorkflowDirectory), cts.Token);
        await WaitForPasteAsync(terminal, 0, cts.Token); // the repair session after the one more attempt failed
        Assert.Contains("AUSFUEHRUNG <ST-001-alpha>", terminal.Pasted[0], StringComparison.Ordinal);
        Assert.Single(starter.Launches);
        PublishSubtaskResult(tracking, "ST-001-alpha", "complete");
        await WaitForPasteAsync(terminal, 1, cts.Token);
        Assert.Contains("AUSFUEHRUNG <ST-002-beta>", terminal.Pasted[1], StringComparison.Ordinal);
        PublishSubtaskResult(tracking, "ST-002-beta", "complete");
        await run;

        // Assert
        Assert.Single(starter.Launches);
    }

    [Fact]
    public async Task PhaseFour_DesktopJob_TwoUserInputAbortsInOneRun_DoNotCarryIntoTheNextRun()
    {
        // Spec AC4: the aborted_user_input exhaustion is per run; a new run gets a fresh attempt budget.

        // Arrange
        var tracking = StageDeferredDesktopJob();
        var monitor = new FakeUserActivityMonitor();
        var starter = new FakeDesktopJobProcessStarter
        {
            ExitCode = null,
            OnStart = _ => monitor.LastWatch!.ReportMouse(flags: 0), // OnStart runs after the runner opened the watch
        };
        var runner = new DesktopJobRunner(monitor, new FakeCpuLoadProbe(), starter, new FakeDesktopJobClock());
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var orchestrator = CreateDesktopJobOrchestrator(runner);

        // Act - run 1: two aborts by real input exhaust the job.
        var first = orchestrator.RunAsync(
            CreateSubtaskRequest(terminal, new ManualPhaseSignal(), tracking.WorkflowDirectory), cts.Token);
        await first;
        Assert.Equal(2, starter.Launches.Count);
        Assert.Empty(terminal.StartedSessions);
        Assert.Contains(
            "Desktop-Job ST-001-alpha/serie erschöpft: aborted_user_input",
            SubtaskReports()[^1].BlockedReason,
            StringComparison.Ordinal);

        // Act - run 2: the same request runs again, undisturbed this time.
        starter.OnStart = null;
        starter.ExitCode = 0;
        starter.MarkerFor = _ => Path.Combine(_root, "serie.marker");
        var second = orchestrator.RunAsync(
            CreateSubtaskRequest(terminal, new ManualPhaseSignal(), tracking.WorkflowDirectory), cts.Token);
        await WaitForPasteAsync(terminal, 0, cts.Token);
        Assert.Contains("AUSFUEHRUNG <ST-001-alpha>", terminal.Pasted[0], StringComparison.Ordinal);
        Assert.Equal(3, starter.Launches.Count);
        PublishSubtaskResult(tracking, "ST-001-alpha", "complete");
        await WaitForPasteAsync(terminal, 1, cts.Token);
        Assert.Contains("AUSFUEHRUNG <ST-002-beta>", terminal.Pasted[1], StringComparison.Ordinal);
        PublishSubtaskResult(tracking, "ST-002-beta", "complete");
        await second;

        // Assert
        Assert.Contains(_state.Recorded, r => r.Phase == WorkflowPhase.Implementation && r.Status == PhaseStatus.Completed);
    }

    /// <summary>Runs a staged desktop job whose idle window never opens and returns its waiting message.</summary>
    private async Task<string> FirstWaitingMessageAsync(SubtaskPaths tracking, UserActivity activity)
    {
        var starter = new FakeDesktopJobProcessStarter();
        var runner = new DesktopJobRunner(
            new FakeUserActivityMonitor { Activity = activity }, new FakeCpuLoadProbe(), starter, new FakeDesktopJobClock());
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var reports = new List<string?>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var request = CreateSubtaskRequest(terminal, new ManualPhaseSignal(), tracking.WorkflowDirectory) with
        {
            LauncherStatus = RecordInto(reports),
        };

        static bool IsWaiting(string? report) =>
            report is not null && report.StartsWith("Desktop-Job ST-001-alpha wartet auf Leerlauf", StringComparison.Ordinal);

        var run = CreateDesktopJobOrchestrator(runner).RunAsync(request, cts.Token);
        await WaitUntil(
            () =>
            {
                lock (reports)
                {
                    return reports.Exists(IsWaiting);
                }
            },
            cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Empty(starter.Launches);
        lock (reports)
        {
            Assert.Null(reports[^1]);
            return reports.First(IsWaiting)!; // IsWaiting matches only non-null reports
        }
    }

    private static void SeedDesktopJobResult(SubtaskPaths tracking, bool foreignRequestId, bool foreignConfigHash, params DesktopJobStepResult[] steps)
    {
        var directory = tracking.SubtaskDirectory("ST-001-alpha");
        var request = DesktopJobFiles.ReadRequest(directory).Request;
        Assert.NotNull(request);
        DesktopJobFiles.WriteResult(
            directory,
            new DesktopJobResult(
                foreignRequestId ? "11111111-1111-1111-1111-111111111111" : request.RequestId.ToString("D"),
                foreignConfigHash ? new string('A', 64) : DesktopJobFiles.ConfigSha256(request),
                steps));
    }

    private static void SeedRawDesktopJobResult(SubtaskPaths tracking, string json)
    {
        var path = DesktopJobFiles.AuthoritativeResultPath(tracking.SubtaskDirectory("ST-001-alpha"));
        var folder = Path.GetDirectoryName(path);
        Assert.NotNull(folder);
        Directory.CreateDirectory(folder);
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    private static DesktopJobStepResult Attempt(string stepId, string outcome, int startHour, int minutes, int attempts)
    {
        var started = new DateTimeOffset(2026, 10, 20, startHour, 0, 0, TimeSpan.Zero);
        return new DesktopJobStepResult(stepId, null, outcome, null, false, started, started.AddMinutes(minutes), attempts);
    }

    [Fact]
    public async Task PhaseFour_DesktopJob_WaitingWithoutARecordedAttempt_ReportsTheUnchangedMessage()
    {
        // Arrange
        var tracking = StageDeferredDesktopJob();

        // Act
        var message = await FirstWaitingMessageAsync(tracking, new UserActivity(TimeSpan.Zero, false, false));

        // Assert
        Assert.Equal("Desktop-Job ST-001-alpha wartet auf Leerlauf (seit 0 min ohne Eingabe, 15 min nötig).", message);
    }

    [Fact]
    public async Task PhaseFour_DesktopJob_WaitingAfterAnAbortedAttempt_NamesItAfterTheLockState()
    {
        // Arrange
        var tracking = StageDeferredDesktopJob();
        SeedDesktopJobResult(tracking, foreignRequestId: false, foreignConfigHash: false, Attempt("serie", DesktopJobOutcome.AbortedUserInput, 7, 12, 2));

        // Act
        var message = await FirstWaitingMessageAsync(tracking, new UserActivity(TimeSpan.Zero, true, false));

        // Assert
        Assert.Equal(
            "Desktop-Job ST-001-alpha wartet auf Leerlauf (seit 0 min ohne Eingabe, 15 min nötig, Sitzung gesperrt; letzter Versuch: aborted_user_input nach 12 min, 2 Versuche).",
            message);
    }

    [Theory]
    [InlineData(DesktopJobOutcome.AbortedBusy)]
    [InlineData(DesktopJobOutcome.StartFailed)]
    [InlineData(DesktopJobOutcome.AbortedUserInput)] // recorded before launch: EndedUtc == StartedUtc (DesktopJobRunner.RunStepAsync)
    public async Task PhaseFour_DesktopJob_WaitingAfterAnAttemptThatRanNothing_OmitsTheDuration(string outcome)
    {
        // Arrange
        var tracking = StageDeferredDesktopJob();
        SeedDesktopJobResult(tracking, foreignRequestId: false, foreignConfigHash: false, Attempt("serie", outcome, 7, 0, 1));

        // Act
        var message = await FirstWaitingMessageAsync(tracking, new UserActivity(TimeSpan.Zero, false, false));

        // Assert
        Assert.Equal(
            $"Desktop-Job ST-001-alpha wartet auf Leerlauf (seit 0 min ohne Eingabe, 15 min nötig; letzter Versuch: {outcome}, 1 Versuch).",
            message);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PhaseFour_DesktopJob_WaitingAfterAPreviousRequestTimedOut_NamesItsLatestAttemptAsThePreviousRequest(bool foreignRequestId, bool foreignConfigHash)
    {
        // Arrange
        // The later attempt is listed first: "last" means latest EndedUtc, not last in the list.
        // One row per half of MatchingResult: a check that ignores either the id or the config hash fails a row.
        var tracking = StageDeferredDesktopJob();
        SeedDesktopJobResult(
            tracking,
            foreignRequestId,
            foreignConfigHash,
            Attempt("serie", DesktopJobOutcome.Timeout, 7, 45, 2),
            Attempt("vorbereitung", DesktopJobOutcome.Completed, 6, 30, 1));

        // Act
        var message = await FirstWaitingMessageAsync(tracking, new UserActivity(TimeSpan.Zero, false, false));

        // Assert
        Assert.Equal(
            "Desktop-Job ST-001-alpha wartet auf Leerlauf (seit 0 min ohne Eingabe, 15 min nötig; letzter Versuch (vorige Anfrage): timeout nach 45 min, 2 Versuche).",
            message);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"requestId":"11111111-1111-1111-1111-111111111111","configSha256":"","steps":null}""")]
    [InlineData("""{"requestId":"11111111-1111-1111-1111-111111111111","configSha256":"","steps":[null]}""")]
    public async Task PhaseFour_DesktopJob_WaitingWithADefectiveResult_ReportsTheUnchangedMessage(string json)
    {
        // Arrange
        // A status text must never end the wait (A2). A foreign request id keeps every other reader of
        // the result (MatchingResult is null) away from the null element, so only LastAttemptNote sees it.
        var tracking = StageDeferredDesktopJob();
        SeedRawDesktopJobResult(tracking, json);

        // Act
        var message = await FirstWaitingMessageAsync(tracking, new UserActivity(TimeSpan.Zero, false, false));

        // Assert
        Assert.Equal("Desktop-Job ST-001-alpha wartet auf Leerlauf (seit 0 min ohne Eingabe, 15 min nötig).", message);
    }

    [Fact]
    public async Task PhaseFour_DesktopJob_ARepairThatKeepsTheTimedOutScriptAndLimitIsNotRunAgainInTheSameRun()
    {
        // Arrange - the runner's 30-second monitor interval makes a 1-minute timeout take two fake polls.
        var tracking = StageDeferredDesktopJob(timeoutMinutes: 1);
        var starter = new FakeDesktopJobProcessStarter { ExitCode = null }; // never exits: each launch times out
        var runner = new DesktopJobRunner(
            new FakeUserActivityMonitor(), new FakeCpuLoadProbe(), starter, new FakeDesktopJobClock(),
            monitorInterval: TimeSpan.FromSeconds(30));
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = CreateDesktopJobOrchestrator(runner).RunAsync(
            CreateSubtaskRequest(terminal, new ManualPhaseSignal(), tracking.WorkflowDirectory), cts.Token);
        await WaitForPasteAsync(terminal, 0, cts.Token); // the repair session after the first timeout
        Assert.Single(starter.Launches);

        // Act - the repair writes a new request with the same script bytes and the same limit.
        WriteDesktopJobRequest(tracking, "ST-001-alpha", timeoutMinutes: 1);
        PublishSubtaskResult(tracking, "ST-001-alpha", "deferred");
        await run;

        // Assert
        Assert.Single(starter.Launches);
        Assert.Single(terminal.StartedSessions);
        Assert.Contains(
            "Desktop-Job ST-001-alpha/serie nicht erneut gestartet: dasselbe Skript mit timeoutMinutes 1 endete in diesem Lauf schon mit timeout",
            SubtaskReports()[^1].BlockedReason,
            StringComparison.Ordinal);
        Assert.DoesNotContain(terminal.Pasted, p => p.Contains("AUSFUEHRUNG <ST-002-beta>", StringComparison.Ordinal));
        Assert.DoesNotContain(
            _state.Recorded,
            r => r.Phase == WorkflowPhase.Implementation && r.Status == PhaseStatus.Completed);
    }

    [Fact]
    public async Task PhaseFour_DesktopJob_ARepairThatRaisesTheTimeoutRunsTheJobAgain()
    {
        // Arrange - the runner's 30-second monitor interval makes a 1-minute timeout take two fake polls.
        var tracking = StageDeferredDesktopJob(timeoutMinutes: 1);
        var starter = new FakeDesktopJobProcessStarter { ExitCode = null }; // never exits: each launch times out
        var runner = new DesktopJobRunner(
            new FakeUserActivityMonitor(), new FakeCpuLoadProbe(), starter, new FakeDesktopJobClock(),
            monitorInterval: TimeSpan.FromSeconds(30));
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = CreateDesktopJobOrchestrator(runner).RunAsync(
            CreateSubtaskRequest(terminal, new ManualPhaseSignal(), tracking.WorkflowDirectory), cts.Token);
        await WaitForPasteAsync(terminal, 0, cts.Token); // the repair session after the first timeout
        Assert.Single(starter.Launches);

        // Act - the repair raises the limit; this time the script succeeds.
        starter.ExitCode = 0;
        starter.MarkerFor = _ => Path.Combine(_root, "serie.marker");
        WriteDesktopJobRequest(tracking, "ST-001-alpha", timeoutMinutes: 2);
        PublishSubtaskResult(tracking, "ST-001-alpha", "deferred");
        await WaitForPasteAsync(terminal, 1, cts.Token); // the verification session
        PublishSubtaskResult(tracking, "ST-001-alpha", "complete");
        await WaitForPasteAsync(terminal, 2, cts.Token);
        PublishSubtaskResult(tracking, "ST-002-beta", "complete");
        await run;

        // Assert
        Assert.Equal(2, starter.Launches.Count);
        Assert.Contains("AUSFUEHRUNG <ST-002-beta>", terminal.Pasted[2], StringComparison.Ordinal);
        Assert.Contains(
            _state.Recorded,
            r => r.Phase == WorkflowPhase.Implementation && r.Status == PhaseStatus.Completed);
    }

    [Fact]
    public async Task PhaseFour_DesktopJob_ARepairThatChangesTheTimedOutScriptRunsTheJobAgain()
    {
        // Arrange - the runner's 30-second monitor interval makes a 1-minute timeout take two fake polls.
        var tracking = StageDeferredDesktopJob(timeoutMinutes: 1);
        var starter = new FakeDesktopJobProcessStarter { ExitCode = null }; // never exits: each launch times out
        var runner = new DesktopJobRunner(
            new FakeUserActivityMonitor(), new FakeCpuLoadProbe(), starter, new FakeDesktopJobClock(),
            monitorInterval: TimeSpan.FromSeconds(30));
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = CreateDesktopJobOrchestrator(runner).RunAsync(
            CreateSubtaskRequest(terminal, new ManualPhaseSignal(), tracking.WorkflowDirectory), cts.Token);
        await WaitForPasteAsync(terminal, 0, cts.Token); // the repair session after the first timeout
        Assert.Single(starter.Launches);

        // Act - the repair keeps the limit but changes the script; this time it succeeds.
        starter.ExitCode = 0;
        starter.MarkerFor = _ => Path.Combine(_root, "serie.marker");
        WriteDesktopJobRequest(tracking, "ST-001-alpha", timeoutMinutes: 1, script: "Write-Output 'repariert'");
        PublishSubtaskResult(tracking, "ST-001-alpha", "deferred");
        await WaitForPasteAsync(terminal, 1, cts.Token); // the verification session
        PublishSubtaskResult(tracking, "ST-001-alpha", "complete");
        await WaitForPasteAsync(terminal, 2, cts.Token);
        PublishSubtaskResult(tracking, "ST-002-beta", "complete");
        await run;

        // Assert
        Assert.Equal(2, starter.Launches.Count);
        Assert.Contains("AUSFUEHRUNG <ST-002-beta>", terminal.Pasted[2], StringComparison.Ordinal);
        Assert.Contains(
            _state.Recorded,
            r => r.Phase == WorkflowPhase.Implementation && r.Status == PhaseStatus.Completed);
    }

    [Fact]
    public async Task PhaseFour_DesktopJob_ATimeoutRecordedByAnEarlierRunIsNotRepeatedByItsRepair()
    {
        // Arrange - the timeout an earlier run left for the current request and script (MP_START W3).
        var tracking = StageDeferredDesktopJob();
        var directory = tracking.SubtaskDirectory("ST-001-alpha");
        var request = DesktopJobFiles.ReadRequest(directory).Request;
        Assert.NotNull(request);
        var started = new DateTimeOffset(2026, 10, 20, 7, 0, 0, TimeSpan.Zero);
        DesktopJobFiles.WriteResult(
            directory,
            new DesktopJobResult(
                request.RequestId.ToString("D"),
                DesktopJobFiles.ConfigSha256(request),
                [new DesktopJobStepResult("serie", DesktopJobFiles.ScriptSha256(directory, "desktopjob.serie.ps1"), DesktopJobOutcome.Timeout, null, false, started, started.AddMinutes(45), 2)]));
        var starter = new FakeDesktopJobProcessStarter { ExitCode = null }; // never exits: each launch times out
        var runner = new DesktopJobRunner(
            new FakeUserActivityMonitor(), new FakeCpuLoadProbe(), starter, new FakeDesktopJobClock(),
            monitorInterval: TimeSpan.FromSeconds(30));
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = CreateDesktopJobOrchestrator(runner).RunAsync(
            CreateSubtaskRequest(terminal, new ManualPhaseSignal(), tracking.WorkflowDirectory), cts.Token);
        await WaitForPasteAsync(terminal, 0, cts.Token); // the repair session, before any job
        Assert.Empty(starter.Launches);

        // Act - the repair writes a new request id with the same script and the same 45 minutes.
        WriteDesktopJobRequest(tracking, "ST-001-alpha");
        PublishSubtaskResult(tracking, "ST-001-alpha", "deferred");
        await run;

        // Assert
        Assert.Empty(starter.Launches);
        Assert.Single(terminal.StartedSessions);
        Assert.Contains(
            "Desktop-Job ST-001-alpha/serie nicht erneut gestartet: dasselbe Skript mit timeoutMinutes 45 endete in diesem Lauf schon mit timeout",
            SubtaskReports()[^1].BlockedReason,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PhaseFour_DesktopJob_ANewRunMayRunTheRefusedRequestOnce()
    {
        // Pins the user's "in the same run" scope (spec N1): the memory of timed-out scripts is local
        // to one run, so the next Continue may run the refused request once. As in
        // PhaseFour_SubtaskLoop_ASecondRunRetriesTheEntryTheFirstRunFailed, both runs share one
        // orchestrator, the shape the application has: a memory promoted to a field fails here.

        // Arrange - the runner's 30-second monitor interval makes a 1-minute timeout take two fake polls.
        var tracking = StageDeferredDesktopJob(timeoutMinutes: 1);
        var starter = new FakeDesktopJobProcessStarter { ExitCode = null }; // never exits: each launch times out
        var runner = new DesktopJobRunner(
            new FakeUserActivityMonitor(), new FakeCpuLoadProbe(), starter, new FakeDesktopJobClock(),
            monitorInterval: TimeSpan.FromSeconds(30));
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var orchestrator = CreateDesktopJobOrchestrator(runner);
        var first = orchestrator.RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory), cts.Token);
        await WaitForPasteAsync(terminal, 0, cts.Token); // the repair session after the first timeout
        WriteDesktopJobRequest(tracking, "ST-001-alpha", timeoutMinutes: 1);
        PublishSubtaskResult(tracking, "ST-001-alpha", "deferred");
        await first;
        Assert.Single(starter.Launches);

        // Act - a new run on the same orchestrator.
        var second = orchestrator.RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory), cts.Token);
        await WaitForPasteAsync(terminal, 1, cts.Token); // the repair session after the second timeout
        PublishSubtaskResult(tracking, "ST-001-alpha", "failed");
        await second;

        // Assert
        Assert.Equal(2, starter.Launches.Count);
        Assert.Contains("AUSFUEHRUNG <ST-001-alpha>", terminal.Pasted[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task PhaseFour_DesktopJob_ARepairThatOnlyRenamesTheTimedOutStepIsNotRunAgain()
    {
        // Arrange - the runner's 30-second monitor interval makes a 1-minute timeout take two fake polls.
        var tracking = StageDeferredDesktopJob(timeoutMinutes: 1);
        var starter = new FakeDesktopJobProcessStarter { ExitCode = null }; // never exits: each launch times out
        var runner = new DesktopJobRunner(
            new FakeUserActivityMonitor(), new FakeCpuLoadProbe(), starter, new FakeDesktopJobClock(),
            monitorInterval: TimeSpan.FromSeconds(30));
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = CreateDesktopJobOrchestrator(runner).RunAsync(
            CreateSubtaskRequest(terminal, new ManualPhaseSignal(), tracking.WorkflowDirectory), cts.Token);
        await WaitForPasteAsync(terminal, 0, cts.Token); // the repair session after the first timeout
        Assert.Single(starter.Launches);

        // Act - the repair renames the step only: same script file, same bytes, same limit.
        WriteDesktopJobRequest(tracking, "ST-001-alpha", timeoutMinutes: 1, stepId: "lauf");
        PublishSubtaskResult(tracking, "ST-001-alpha", "deferred");
        await run;

        // Assert - the step id is not part of the signature.
        Assert.Single(starter.Launches);
        Assert.Contains(
            "Desktop-Job ST-001-alpha/lauf nicht erneut gestartet: dasselbe Skript mit timeoutMinutes 1 endete in diesem Lauf schon mit timeout",
            SubtaskReports()[^1].BlockedReason,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PhaseFour_DesktopJob_AScriptRevertedDuringTheIdleWaitIsRefusedAtTheLaunch()
    {
        // Arrange - the runner's 30-second monitor interval makes a 1-minute timeout take two fake polls.
        var tracking = StageDeferredDesktopJob(timeoutMinutes: 1);
        var monitor = new FakeUserActivityMonitor();
        var clock = new FakeDesktopJobClock();
        var starter = new FakeDesktopJobProcessStarter { ExitCode = null }; // never exits: each launch times out
        var runner = new DesktopJobRunner(
            monitor, new FakeCpuLoadProbe(), starter, clock,
            monitorInterval: TimeSpan.FromSeconds(30));
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = CreateDesktopJobOrchestrator(runner).RunAsync(
            CreateSubtaskRequest(terminal, new ManualPhaseSignal(), tracking.WorkflowDirectory), cts.Token);
        await WaitForPasteAsync(terminal, 0, cts.Token); // the repair session after the first timeout
        Assert.Single(starter.Launches);

        // Armed only now: before, the clock's delays served the runner's monitor polls of the first launch.
        // The repair session's stall watch shares the clock but never reads the activity monitor, so a
        // delay after a fresh monitor read is the orchestrator's idle poll, which runs after the check
        // before the idle wait. During the first one the script reverts to the bytes that timed out, and
        // the user leaves.
        var readsBeforeRepair = monitor.Reads;
        var pollsAfterRepair = 0;
        clock.OnDelay = _ =>
        {
            if (monitor.Reads == readsBeforeRepair)
            {
                return;
            }

            pollsAfterRepair++;
            if (pollsAfterRepair == 1)
            {
                File.WriteAllText(
                    Path.Combine(tracking.SubtaskDirectory("ST-001-alpha"), "desktopjob.serie.ps1"),
                    "Write-Output 'serie'");
                monitor.Activity = new UserActivity(TimeSpan.FromHours(1), false, false);
            }
        };

        // Act - the repair changes the script, so the check before the idle wait lets the job through;
        // the user is active, so the job waits for the idle window.
        monitor.Activity = new UserActivity(TimeSpan.Zero, false, false);
        WriteDesktopJobRequest(tracking, "ST-001-alpha", timeoutMinutes: 1, script: "Write-Output 'repariert'");
        PublishSubtaskResult(tracking, "ST-001-alpha", "deferred");
        await run;

        // Assert
        Assert.Single(starter.Launches);
        Assert.True(pollsAfterRepair >= 1);
        Assert.Contains(
            "Desktop-Job ST-001-alpha/serie nicht erneut gestartet: dasselbe Skript mit timeoutMinutes 1 endete in diesem Lauf schon mit timeout",
            SubtaskReports()[^1].BlockedReason,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PhaseFour_ExecutionGate_ASessionWaitsWhileADesktopJobHoldsTheGate()
    {
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();
        WriteSubtaskRunPrompt();
        WriteIndex(tracking, "ST-001-alpha");
        WriteDescription(tracking, "ST-001-alpha", "Beschreibung ALPHA-ONLY");

        var gate = new ExecutionGate();
        var job = await gate.AcquireExclusiveAsync(CancellationToken.None);

        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var orchestrator = CreateDesktopJobOrchestrator(
            new DesktopJobRunner(new FakeUserActivityMonitor(), new FakeCpuLoadProbe(), new FakeDesktopJobProcessStarter()),
            gate);
        var run = orchestrator.RunAsync(
            CreateSubtaskRequest(terminal, new ManualPhaseSignal(), tracking.WorkflowDirectory), cts.Token);

        await Task.Delay(200);
        Assert.Empty(terminal.StartedSessions);

        job.Dispose();

        await WaitForPasteAsync(terminal, 0, cts.Token);
        PublishSubtaskResult(tracking, "ST-001-alpha", "complete");
        await run;

        Assert.Single(terminal.StartedSessions);
    }

    // --- Phase 4, status settling and published progress (task 4.4) -----------------------

    /// <summary>The ledger's reason for a status payload that never became readable.</summary>
    private const string UnusableStatusFragment = "unlesbar oder fehlerhaft";

    /// <summary>
    /// Stands in for the wait between two settling reads. It records every interval the
    /// orchestrator asks for - so the contract's 200 ms is asserted rather than merely slept - and
    /// lets a test change the tracking files between two reads, which makes the retry sequence
    /// deterministic instead of a race against the wall clock.
    /// </summary>
    private sealed class SettleDelayRecorder(Action<int>? onDelay = null)
    {
        private readonly List<TimeSpan> _requested = [];

        /// <summary>Every interval the orchestrator asked to wait, in order.</summary>
        public TimeSpan[] Requested
        {
            get
            {
                lock (_requested)
                {
                    return [.. _requested];
                }
            }
        }

        /// <summary>The delegate handed to the orchestrator in place of <see cref="Task.Delay(TimeSpan)"/>.</summary>
        public Task DelayAsync(TimeSpan interval, CancellationToken cancellationToken)
        {
            int ordinal;
            lock (_requested)
            {
                _requested.Add(interval);
                ordinal = _requested.Count;
            }

            cancellationToken.ThrowIfCancellationRequested();
            onDelay?.Invoke(ordinal);

            return Task.CompletedTask;
        }
    }

    /// <summary>Writes a status payload and then the completion flag, as a session publishes them.</summary>
    private static void PublishRawStatusAndFlag(SubtaskPaths tracking, string title, string statusJson)
    {
        WriteStatus(tracking, title, statusJson);
        File.WriteAllText(tracking.SubtaskResultFile(title), $$"""{"subtask":"{{title}}"}""");
    }

    /// <summary>
    /// The fixture's rule set with a short auto-answer settle ceiling, for the tests below.
    /// </summary>
    /// <remarks>
    /// Identical to the shared fixture in every dimension these tests touch - same rule, same quiet
    /// periods, same paste and submit timings - so nothing they assert is affected. Only the
    /// ceiling is short. A <see cref="FakeTerminalController"/> that never renders leaves the
    /// auto-answer loop with nothing to settle on, so the shipped 2000 ms is spent, per session, in
    /// a 50 ms poll that none of these tests examines. Across their nine sessions that is twenty
    /// seconds of the class's wall clock and some four hundred pool continuations, which is load
    /// every other test in the assembly then has to share a machine with. Settling the *status
    /// payload* - what these tests do pin - is driven by the injected waiter, not by this file.
    /// </remarks>
    private const string ShortAutoAnswerCeilingRules = """
    {
      "version": 2, "quietPeriodMs": 10, "settleTimeoutMs": 60, "maxAnswersPerPhase": 3,
      "pasteQuietPeriodMs": 10, "pasteSettleTimeoutMs": 300, "submitVerifyMs": 30, "maxSubmitAttempts": 2,
      "rules": [ { "id": "bypass", "pattern": "(?is)bypass", "send": "\\e[B\\r", "description": "" } ]
    }
    """;

    /// <summary>The orchestrator the settling and progress tests below run against.</summary>
    private WorkflowOrchestrator CreateSettlingOrchestrator(
        Func<TimeSpan, CancellationToken, Task>? settleDelay = null) =>
        CreateOrchestratorWithRules(ShortAutoAnswerCeilingRules, settleDelay);

    [Fact]
    public async Task PhaseFour_SubtaskSettling_TakesTheParsedStateWhenTheThirdReadSucceeds()
    {
        // Requirement 2.11 and design decision (issue 10), verbatim observable: a status file that
        // is unparseable on the first read but valid on the third attempt yields the PARSED state
        // rather than a failure. An orchestrator that read the payload once would publish Failed
        // here, which is the mutation this test exists to catch.
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();
        WriteSubtaskRunPrompt();

        WriteIndex(tracking, "ST-001-alpha");
        WriteDescription(tracking, "ST-001-alpha", "Beschreibung ALPHA-ONLY");

        // The SECOND wait lands the finished payload: read 1 and read 2 see the half-written file,
        // read 3 - the third attempt - sees valid JSON. Driving it from the wait rather than from a
        // timer is what makes "the third attempt" an exact statement.
        var delays = new SettleDelayRecorder(ordinal =>
        {
            if (ordinal == 2)
            {
                WriteStatus(tracking, "ST-001-alpha", """{"status":"complete"}""");
            }
        });

        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var run = CreateSettlingOrchestrator(delays.DelayAsync).RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory), cts.Token);

        await WaitForPasteAsync(terminal, 0, cts.Token);

        // The agent's temp-file rename has not landed: the flag is already there, the payload is a
        // truncated fragment.
        PublishRawStatusAndFlag(tracking, "ST-001-alpha", """{"status":"comp""");

        await run;

        // The reused index's counts, then one report before the session and one after it.
        var reports = SubtaskReports();
        Assert.Equal(3, reports.Length);

        var settled = reports[^1];
        Assert.Equal(SubtaskStage.Running, settled.Stage);
        Assert.Equal("ST-001-alpha", settled.CurrentTitle);
        Assert.Equal(SubtaskStatus.Complete, Assert.Single(settled.States).Status);
        Assert.Equal(1, settled.Completed);
        Assert.Equal(0, settled.Failed);

        // Exactly two waits: settling stopped the moment the parse succeeded, and never ran on to
        // the ceiling.
        Assert.Equal(2, delays.Requested.Length);
        Assert.All(delays.Requested, interval => Assert.Equal(TimeSpan.FromMilliseconds(200), interval));
    }

    [Fact]
    public async Task PhaseFour_SubtaskSettling_RetriesFiveTimesAndThenLetsTheLedgerFailTheEntry()
    {
        // Requirement 2.11 and E7: the bound is five retries after the first read, at 200 ms. The
        // payload never becomes readable, so the entry is left as the ledger finds it - Failed,
        // carrying the ledger's own reason - and the run ends instead of waiting for ever. The
        // exact wait count is what pins the ceiling: four retries or six both fail here.
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();
        WriteSubtaskRunPrompt();

        WriteIndex(tracking, "ST-001-alpha");
        WriteDescription(tracking, "ST-001-alpha", "Beschreibung ALPHA-ONLY");

        var delays = new SettleDelayRecorder();

        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var run = CreateSettlingOrchestrator(delays.DelayAsync).RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory), cts.Token);

        await WaitForPasteAsync(terminal, 0, cts.Token);

        PublishRawStatusAndFlag(tracking, "ST-001-alpha", "{ dies ist kein JSON");

        await run;

        Assert.Equal(5, delays.Requested.Length);
        Assert.All(delays.Requested, interval => Assert.Equal(TimeSpan.FromMilliseconds(200), interval));

        // The last report about the entry itself; the run's closing report names no entry.
        var reports = SubtaskReports();
        var settled = reports.Last(report => report.CurrentTitle is not null);

        Assert.Equal("ST-001-alpha", settled.CurrentTitle);
        Assert.Equal(1, settled.Failed);
        Assert.Equal(0, settled.Completed);

        var state = Assert.Single(settled.States);
        Assert.Equal(SubtaskStatus.Failed, state.Status);
        Assert.Contains(UnusableStatusFragment, state.FailReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PhaseFour_SubtaskSettling_StopsOnAParsedPendingPayloadInsteadOfWaitingForAStateChange()
    {
        // Design decision (issue 10): settling ends on a successful PARSE, not on "the state is no
        // longer Pending". A payload that parses and says pending is a settled outcome, so not one
        // single wait may be spent on it - an implementation that waited for a state change would
        // burn the whole ceiling here. Requirement 3.3 then holds: pending counts as pending, and
        // the published failure count stays 0.
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();
        WriteSubtaskRunPrompt();

        WriteIndex(tracking, "ST-001-alpha");
        WriteDescription(tracking, "ST-001-alpha", "Beschreibung ALPHA-ONLY");

        var delays = new SettleDelayRecorder();

        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var run = CreateSettlingOrchestrator(delays.DelayAsync).RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory), cts.Token);

        await WaitForPasteAsync(terminal, 0, cts.Token);

        PublishRawStatusAndFlag(tracking, "ST-001-alpha", """{"status":"pending"}""");

        await run;

        Assert.Empty(delays.Requested);

        // The last report about the entry itself; the run's closing report names no entry.
        var reports = SubtaskReports();
        var settled = reports.Last(report => report.CurrentTitle is not null);

        Assert.Equal("ST-001-alpha", settled.CurrentTitle);
        Assert.Equal(SubtaskStatus.Pending, Assert.Single(settled.States).Status);
        Assert.Equal(0, settled.Failed);
        Assert.Equal(0, settled.Completed);
    }

    [Fact]
    public async Task PhaseFour_SubtaskProgress_CarriesTheFailureReasonTextFromTheStatusPayload()
    {
        // Requirement 3.7 and design issue 7: the ordered states are the channel that delivers the
        // failure reasons, so the reason the agent wrote must arrive verbatim in the progress
        // published after that subtask. A payload that carried counts only passes every count
        // assertion and fails exactly here.
        const string Reason = "Kompilierung im Modul XY fehlgeschlagen";

        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();
        WriteSubtaskRunPrompt();

        WriteIndexEntries(tracking, Chained("ST-001-alpha"), Independent("ST-002-beta"));
        WriteDescription(tracking, "ST-001-alpha", "Beschreibung ALPHA-ONLY");
        WriteDescription(tracking, "ST-002-beta", "Beschreibung BETA-ONLY");

        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var run = CreateSettlingOrchestrator().RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory), cts.Token);

        await WaitForPasteAsync(terminal, 0, cts.Token);
        PublishRawStatusAndFlag(
            tracking, "ST-001-alpha", $$"""{"status":"failed","failreason":"{{Reason}}"}""");

        await WaitForPasteAsync(terminal, 1, cts.Token);
        PublishRawStatusAndFlag(tracking, "ST-002-beta", """{"status":"complete"}""");

        await run;

        // The reused index's counts, then two reports per entry.
        var reports = SubtaskReports();

        // The refreshed report for the FIRST subtask, not the run's last one: 3.2 asks for a
        // publication after each subtask, so the reason must already be there before the second
        // session starts.
        var afterFirst = reports.LastOrDefault(r => r.CurrentTitle == "ST-001-alpha");
        Assert.NotNull(afterFirst);
        Assert.Equal(1, afterFirst.Failed);

        var failed = Assert.Single(afterFirst.States, state => state.Status == SubtaskStatus.Failed);
        Assert.Equal("ST-001-alpha", failed.Title);
        Assert.Equal(Reason, failed.FailReason);
    }

    [Fact]
    public async Task PhaseFour_SubtaskProgress_PublishesStageCountsTitleAndOrderedStatesAfterEachSubtask()
    {
        // Requirements 2.6, 3.2 and 3.7 together. Every dimension the payload carries is given a
        // distinct value, so no swap survives: nine entries split 4 complete / 3 failed /
        // 2 pending, and 4 + 3 + 2 == 9 is pinned as well, which is what makes a mutant that
        // exchanges the Completed and Failed counters - or Completed and Total - fail here.
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();
        WriteSubtaskRunPrompt();

        string[] titles =
        [
            "ST-01-ok", "ST-02-ok", "ST-03-ok", "ST-04-ok",
            "ST-05-kaputt", "ST-06-kaputt", "ST-07-kaputt",
            "ST-08-offen", "ST-09-offen",
        ];

        // Every unfinished entry is independent, so neither the failures nor the pending eighth
        // entry holds back the ones after them.
        WriteIndexEntries(tracking, [.. titles[..4].Select(Chained), .. titles[4..].Select(Independent)]);

        // Four already complete: not eligible, so they cost no session.
        foreach (var title in titles[..4])
        {
            StageSubtask(tracking, title, "complete");
        }

        // Three with a folder but no description: eligible, skipped as failures, still no session.
        foreach (var title in titles[4..7])
        {
            Directory.CreateDirectory(tracking.SubtaskDirectory(title));
        }

        // Two untouched entries with descriptions: exactly these two get sessions.
        WriteDescription(tracking, "ST-08-offen", "Beschreibung ACHT-ONLY");
        WriteDescription(tracking, "ST-09-offen", "Beschreibung NEUN-ONLY");

        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var run = CreateSettlingOrchestrator().RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory), cts.Token);

        await WaitForPasteAsync(terminal, 0, cts.Token);
        Assert.Contains("AUSFUEHRUNG <ST-08-offen>", terminal.Pasted[0], StringComparison.Ordinal);

        // Still pending afterwards, so the counts below stay 4 / 3 / 2 and remain distinct.
        PublishRawStatusAndFlag(tracking, "ST-08-offen", """{"status":"pending"}""");

        await WaitForPasteAsync(terminal, 1, cts.Token);
        PublishRawStatusAndFlag(tracking, "ST-09-offen", """{"status":"complete"}""");

        await run;

        var reports = SubtaskReports();
        var afterEighth = reports.LastOrDefault(r => r.CurrentTitle == "ST-08-offen");
        Assert.NotNull(afterEighth);

        Assert.Equal(SubtaskStage.Running, afterEighth.Stage);
        Assert.Equal(4, afterEighth.Completed);
        Assert.Equal(9, afterEighth.Total);
        Assert.Equal(3, afterEighth.Failed);

        // The states arrive complete and in index order, which is what lets the indicator render a
        // per-entry tooltip at all.
        Assert.Equal(titles, afterEighth.States.Select(state => state.Title));
        Assert.Equal(2, afterEighth.States.Count(state => state.Status == SubtaskStatus.Pending));
        Assert.Equal(afterEighth.Total, afterEighth.Completed + afterEighth.Failed
            + afterEighth.States.Count(state => state.Status == SubtaskStatus.Pending));
    }

    [Fact]
    public async Task PhaseFour_SubtaskProgress_ReplacesTheDecomposingStageBeforeTheFirstSession()
    {
        // A reusing run never reports Decomposing: the index already exists, so its first report is
        // the counts read from it, published before anything else can hold the run (task 4.2,
        // requirement 3.2). The first live report must already be out by the time the first prompt
        // is pasted, because the first session can take an hour.
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();
        WriteSubtaskRunPrompt();

        WriteIndex(tracking, "ST-001-alpha", "ST-002-beta", "ST-003-gamma");
        WriteDescription(tracking, "ST-001-alpha", "Beschreibung ALPHA-ONLY");
        WriteDescription(tracking, "ST-002-beta", "Beschreibung BETA-ONLY");
        StageSubtask(tracking, "ST-003-gamma", "complete");

        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var run = CreateSettlingOrchestrator().RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory), cts.Token);

        await WaitForPasteAsync(terminal, 0, cts.Token);

        var early = SubtaskReports();
        Assert.Equal(2, early.Length);
        Assert.DoesNotContain(early, r => r.Stage == SubtaskStage.Decomposing);

        // The reused index's counts, before any entry is running.
        Assert.Equal(SubtaskStage.Running, early[0].Stage);
        Assert.Null(early[0].CurrentTitle);
        Assert.Equal(1, early[0].Completed);
        Assert.Equal(3, early[0].Total);
        Assert.Equal(0, early[0].Failed);

        // Live counts, and the entry that is running right now - not the one that just finished.
        Assert.Equal(SubtaskStage.Running, early[1].Stage);
        Assert.Equal("ST-001-alpha", early[1].CurrentTitle);
        Assert.Equal(1, early[1].Completed);
        Assert.Equal(3, early[1].Total);
        Assert.Equal(0, early[1].Failed);

        PublishRawStatusAndFlag(tracking, "ST-001-alpha", """{"status":"complete"}""");
        await WaitForPasteAsync(terminal, 1, cts.Token);
        PublishRawStatusAndFlag(tracking, "ST-002-beta", """{"status":"complete"}""");

        await run;
    }

    [Fact]
    public async Task PhaseFour_FreshDecomposition_ReportsDecomposingFirstAndTheNewCountsAfterIt()
    {
        // Arrange - no index yet, so the run decomposes first.
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();
        WriteSubtaskRunPrompt();
        WriteDescription(tracking, "ST-001-alpha", "Beschreibung ST-001-alpha");
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var run = CreateOrchestrator().RunAsync(
            CreateSubtaskRequest(terminal, new ManualPhaseSignal(), tracking.WorkflowDirectory), cts.Token);

        // Act
        await WaitForPasteAsync(terminal, 0, cts.Token);
        Assert.Contains("ZERLEGUNG", terminal.Pasted[0], StringComparison.Ordinal);
        var whileDecomposing = SubtaskReports();

        WriteIndex(tracking, "ST-001-alpha");
        await WaitForPasteAsync(terminal, 1, cts.Token);
        PublishSubtaskResult(tracking, "ST-001-alpha", "complete");
        await run;

        // Assert - Decomposing while the session runs, then the fresh index's counts.
        Assert.Equal(SubtaskStage.Decomposing, Assert.Single(whileDecomposing).Stage);
        var reports = SubtaskReports();
        Assert.Equal(SubtaskStage.Decomposing, reports[0].Stage);
        Assert.Equal(SubtaskStage.Running, reports[1].Stage);
        Assert.Null(reports[1].CurrentTitle);
        Assert.Equal(0, reports[1].Completed);
        Assert.Equal(1, reports[1].Total);
    }

    [Fact]
    public async Task PhaseFour_HoldForDecisionApproval_ShowsTheCountsNotDecomposing()
    {
        // Arrange - a reused index and unapproved decisions: the run holds before the first subtask.
        var tracking = ArrangeDecomposedSubtaskRun("ST-001-alpha", "ST-002-beta");
        StageSubtask(tracking, "ST-002-beta", "complete");
        WriteDecisionsPrompt();
        WriteDecisions(tracking, "## D-001\n- policy: use-default\n");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        var (run, terminal, pause, _) = StartPreflightRun(tracking, cts.Token);
        await WaitUntil(() => pause.IsPaused, cts.Token);

        // Assert - nothing started, and the indicator shows what the index says.
        Assert.Empty(terminal.StartedSessions);
        var reports = SubtaskReports();
        Assert.DoesNotContain(reports, r => r.Stage == SubtaskStage.Decomposing);
        var last = reports[^1];
        Assert.Equal(SubtaskStage.Running, last.Stage);
        Assert.Equal(1, last.Completed);
        Assert.Equal(2, last.Total);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    // --- Phase 4, automatic completion from fresh evidence (task 4.5) ---------------------

    /// <summary>Stages an entry that an earlier run already finished: description plus a status.</summary>
    private static void StageCompletedSubtask(SubtaskPaths tracking, string title, string description)
    {
        WriteDescription(tracking, title, description);
        WriteStatus(tracking, title, """{"status":"complete"}""");
    }

    [Fact]
    public async Task PhaseFour_SubtaskRunWhereEveryEntryCompletes_RecordsImplementationCompleted()
    {
        // Requirement 3.5 and the task's first observable. Three entries, two already complete on
        // disk and the middle one executed here, so the final snapshot is Total 3 / Completed 3 /
        // Failed 0 - three pairwise distinct numbers, which is what makes a decision that compared
        // the wrong pair of counters visible rather than accidentally right (note 1.2).
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();
        WriteSubtaskRunPrompt();

        WriteIndex(tracking, "ST-001-alpha", "ST-002-beta", "ST-003-gamma");
        StageCompletedSubtask(tracking, "ST-001-alpha", "Beschreibung ALPHA-ONLY");
        WriteDescription(tracking, "ST-002-beta", "Beschreibung BETA-ONLY");
        StageCompletedSubtask(tracking, "ST-003-gamma", "Beschreibung GAMMA-ONLY");

        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var run = CreateSettlingOrchestrator().RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory), cts.Token);

        await WaitForPasteAsync(terminal, 0, cts.Token);
        Assert.Contains("AUSFUEHRUNG <ST-002-beta>", terminal.Pasted[0], StringComparison.Ordinal);
        PublishSubtaskResult(tracking, "ST-002-beta", "complete");

        await run;

        Assert.Single(terminal.StartedSessions);

        // The fixture really is the all-complete, failure-free shape the decision was given.
        var final = SubtaskReports()[^1];
        Assert.Equal(3, final.Total);
        Assert.Equal(3, final.Completed);
        Assert.Equal(0, final.Failed);

        // The whole of this task's journal contract: phase entry recorded Active, and the run ends
        // by recording Completed - in that order, and exactly once each. A count and two indexed
        // comparisons rather than Assert.Contains, so a second or misordered write is visible too.
        Assert.Equal(2, _state.Recorded.Count);
        Assert.Equal((WorkflowPhase.Implementation, PhaseStatus.Active), _state.Recorded[0]);
        Assert.Equal((WorkflowPhase.Implementation, PhaseStatus.Completed), _state.Recorded[1]);

        lock (_progress)
        {
            Assert.Contains(
                _progress,
                p => p.Phase == WorkflowPhase.Implementation && p.Status == PhaseStatus.Completed);
        }

        // Automatic completion only; the manual override is the view model's (design issue 6b).
        Assert.Empty(_state.ManualCompletions);
    }

    [Fact]
    public async Task PhaseFour_SubtaskRunWhoseFinalIndexReadFails_LeavesImplementationActive()
    {
        // The task's second observable, requirement 3.6 and design decision (issues 6a and 9): the
        // last snapshot this run held in memory says every entry is complete, and the decision must
        // still refuse, because the read it makes for itself fails.
        //
        // The index is opened for exclusive access from inside the all-complete report, which
        // InlineProgress delivers on the orchestrator's own thread - so the lock is provably in
        // place before the run takes another step, and nothing here depends on winning a race.
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();
        WriteSubtaskRunPrompt();

        WriteIndex(tracking, "ST-001-alpha", "ST-002-beta", "ST-003-gamma");
        StageCompletedSubtask(tracking, "ST-001-alpha", "Beschreibung ALPHA-ONLY");
        WriteDescription(tracking, "ST-002-beta", "Beschreibung BETA-ONLY");
        StageCompletedSubtask(tracking, "ST-003-gamma", "Beschreibung GAMMA-ONLY");

        FileStream? seized = null;
        try
        {
            _afterSubtaskReport = report =>
            {
                if (seized is null && report.Total > 0 && report.Completed == report.Total)
                {
                    seized = new FileStream(
                        tracking.ResultAbsolute, FileMode.Open, FileAccess.Read, FileShare.None);
                }
            };

            var terminal = new FakeTerminalController();
            terminal.ReadyGate.SetResult();
            var signal = new ManualPhaseSignal();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            var run = CreateSettlingOrchestrator().RunAsync(
                CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory), cts.Token);

            await WaitForPasteAsync(terminal, 0, cts.Token);
            PublishSubtaskResult(tracking, "ST-002-beta", "complete");

            await run;

            // The premise: an all-complete snapshot really was the last thing this run knew.
            Assert.NotNull(seized);
            var lastKnown = SubtaskReports()[^1];
            Assert.Equal(3, lastKnown.Total);
            Assert.Equal(3, lastKnown.Completed);
            Assert.Equal(0, lastKnown.Failed);

            // And the index really is unreadable at the moment the decision is taken.
            Assert.Null(SubtaskLedger.TryRead(tracking));

            Assert.DoesNotContain(_state.Recorded, r => r.Status == PhaseStatus.Completed);
            Assert.Contains(
                _state.Recorded,
                r => r.Phase == WorkflowPhase.Implementation && r.Status == PhaseStatus.Active);
        }
        finally
        {
            if (seized is not null)
            {
                await seized.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task PhaseFour_SubtaskRunLeavingPendingEntriesWithoutFailures_LeavesImplementationActive()
    {
        // Requirement 3.6 and design's "'no failures' is not sufficient evidence". Both executed
        // entries publish their completion flag with no status payload beside it, which the resolved
        // vocabulary reads as Pending, not Failed. The final snapshot is therefore
        // Total 3 / Completed 1 / Failed 0 - pairwise distinct, and with zero failures - so a
        // decision that asked "were there failures?" rather than "is every entry complete?" would
        // wrongly record completion here. Gamma is independent, so beta's pending outcome does not
        // hold it back.
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();
        WriteSubtaskRunPrompt();

        WriteIndexEntries(
            tracking, Chained("ST-001-alpha"), Chained("ST-002-beta"), Independent("ST-003-gamma"));
        StageCompletedSubtask(tracking, "ST-001-alpha", "Beschreibung ALPHA-ONLY");
        WriteDescription(tracking, "ST-002-beta", "Beschreibung BETA-ONLY");
        WriteDescription(tracking, "ST-003-gamma", "Beschreibung GAMMA-ONLY");

        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var run = CreateSettlingOrchestrator().RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory), cts.Token);

        await WaitForPasteAsync(terminal, 0, cts.Token);
        PublishSubtaskResult(tracking, "ST-002-beta", status: null);

        await WaitForPasteAsync(terminal, 1, cts.Token);
        PublishSubtaskResult(tracking, "ST-003-gamma", status: null);

        await run;

        Assert.Equal(2, terminal.StartedSessions.Count);

        var final = SubtaskReports()[^1];
        Assert.Equal(3, final.Total);
        Assert.Equal(1, final.Completed);
        Assert.Equal(0, final.Failed);

        Assert.DoesNotContain(_state.Recorded, r => r.Status == PhaseStatus.Completed);
        Assert.Contains(
            _state.Recorded,
            r => r.Phase == WorkflowPhase.Implementation && r.Status == PhaseStatus.Active);
    }

    [Fact]
    public async Task PhaseFour_SubtaskRunThatFinishesWithFailures_LeavesImplementationActive()
    {
        // Requirement 3.6's literal first clause: automatic execution that finishes WITH failures
        // leaves implementation incomplete and the task retained for recovery. Final snapshot
        // Total 3 / Completed 2 / Failed 1 - pairwise distinct once more, and the mirror image of
        // the pending-only run above, so the refusal is pinned for both reasons an entry can be
        // short of Complete rather than only for the one that is easier to get right. Gamma is
        // independent, so beta's failure does not hold it back.
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();
        WriteSubtaskRunPrompt();

        WriteIndexEntries(
            tracking, Chained("ST-001-alpha"), Chained("ST-002-beta"), Independent("ST-003-gamma"));
        StageCompletedSubtask(tracking, "ST-001-alpha", "Beschreibung ALPHA-ONLY");
        WriteDescription(tracking, "ST-002-beta", "Beschreibung BETA-ONLY");
        WriteDescription(tracking, "ST-003-gamma", "Beschreibung GAMMA-ONLY");

        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var run = CreateSettlingOrchestrator().RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory), cts.Token);

        await WaitForPasteAsync(terminal, 0, cts.Token);
        PublishSubtaskResult(tracking, "ST-002-beta", "failed");

        // The failure does not end the run: the remaining independent entry still gets its session.
        await WaitForPasteAsync(terminal, 1, cts.Token);
        PublishSubtaskResult(tracking, "ST-003-gamma", "complete");

        await run;

        Assert.Equal(2, terminal.StartedSessions.Count);

        var final = SubtaskReports()[^1];
        Assert.Equal(3, final.Total);
        Assert.Equal(2, final.Completed);
        Assert.Equal(1, final.Failed);

        Assert.DoesNotContain(_state.Recorded, r => r.Status == PhaseStatus.Completed);
        Assert.Contains(
            _state.Recorded,
            r => r.Phase == WorkflowPhase.Implementation && r.Status == PhaseStatus.Active);
    }

    // The fixture's single rules file is shared by every test in the class; this one needs its
    // own timings, so it gets its own file.
    private WorkflowOrchestrator CreateOrchestratorWithRules(
        string json,
        Func<TimeSpan, CancellationToken, Task>? settleDelay = null)
    {
        var path = Path.Combine(_root, "rules-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);

        return new WorkflowOrchestrator(
            new PromptTemplateService(_promptDir),
            new AutoAnswerService(path, overridePath: null),
            new ArtifactWatcherFactory(),
            _state,
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(40),
            settleDelay,
            requireDecisionApproval: false);
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

    // --- Pause between phases (Workflow_LOAD_AND_PAUSE spec section 6.3) ----------------------

    private Task WaitForPhaseCompletedAsync(WorkflowPhase phase, CancellationToken cancellationToken) =>
        WaitUntil(
            () =>
            {
                lock (_progress)
                {
                    return _progress.Any(p => p.Phase == phase && p.Status == PhaseStatus.Completed);
                }
            },
            cancellationToken);

    [Fact]
    public async Task Pause_HoldsTheNextPhaseUntilUnpaused()
    {
        // Arrange
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var pause = new PhasePauseGate();
        pause.Pause();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = CreateOrchestrator().RunAsync(
            CreateRequest(terminal, new ManualPhaseSignal()) with { Pause = pause }, cts.Token);

        // Act - phase 1 runs although the gate is closed, and completes on its own artefacts.
        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);
        WriteArtifacts(_paths.SpecAbsolute, _paths.PlanAbsolute);
        await WaitForPhaseCompletedAsync(WorkflowPhase.Specification, cts.Token);
        await Task.Delay(500, cts.Token);

        // Assert - held: no second session, phase 2 never reported, the journal resumes at phase 2,
        // and the phase-1 session has not been torn down.
        Assert.Single(terminal.StartedSessions);
        lock (_progress)
        {
            Assert.DoesNotContain(_progress, p => p.Phase == WorkflowPhase.Simplifier);
        }

        var journal = _state.TryLoad(_paths);
        Assert.NotNull(journal);
        Assert.Equal(PhaseStatus.Completed, journal.Phases[(int)WorkflowPhase.Specification].Status);
        Assert.Equal(PhaseStatus.Pending, journal.Phases[(int)WorkflowPhase.Simplifier].Status);
        Assert.Equal(0, terminal.DisposeCount);
        Assert.False(run.IsCompleted);

        // Act - Play.
        pause.Unpause();

        // Assert
        await WaitForPhaseAsync(WorkflowPhase.Simplifier, cts.Token);
        await WaitUntil(() => terminal.StartedSessions.Count >= 2, cts.Token);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Pause_ArmedBeforeTheRun_NeverHoldsTheStartPhase()
    {
        // Arrange - 'Continue workflow' at phase 2 with the pause already armed.
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var pause = new PhasePauseGate();
        pause.Pause();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var request = CreateRequest(terminal, new ManualPhaseSignal()) with
        {
            StartPhase = WorkflowPhase.Review,
            Pause = pause,
        };

        // Act
        var run = CreateOrchestrator().RunAsync(request, cts.Token);

        // Assert - the start phase is what the user just asked for, so it runs.
        await WaitForPhaseAsync(WorkflowPhase.Review, cts.Token);
        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);
        Assert.Single(terminal.StartedSessions);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Pause_DisarmedWhileThePhaseRuns_DoesNotHoldTheNextOne()
    {
        // Arrange
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var pause = new PhasePauseGate();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = CreateOrchestrator().RunAsync(
            CreateRequest(terminal, new ManualPhaseSignal()) with { Pause = pause }, cts.Token);
        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);

        // Act - Pause, then Play, both while phase 1 is still running.
        pause.Pause();
        pause.Unpause();
        WriteArtifacts(_paths.SpecAbsolute, _paths.PlanAbsolute);

        // Assert
        await WaitForPhaseAsync(WorkflowPhase.Simplifier, cts.Token);
        await WaitUntil(() => terminal.StartedSessions.Count >= 2, cts.Token);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Pause_CancellationWhileHeld_EndsTheRunAndDisposesTheSession()
    {
        // Arrange - Review Focus 4: the tab (or the application) is closed while the run is held.
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var pause = new PhasePauseGate();
        pause.Pause();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = CreateOrchestrator().RunAsync(
            CreateRequest(terminal, new ManualPhaseSignal()) with { Pause = pause }, cts.Token);
        await WaitUntil(() => terminal.Pasted.Count >= 1, cts.Token);
        WriteArtifacts(_paths.SpecAbsolute, _paths.PlanAbsolute);
        await WaitForPhaseCompletedAsync(WorkflowPhase.Specification, cts.Token);

        // Act
        await cts.CancelAsync();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(terminal.DisposeCount >= 1);
        Assert.Single(terminal.StartedSessions);
        lock (_progress)
        {
            Assert.DoesNotContain(_progress, p => p.Phase == WorkflowPhase.Simplifier);
        }

        var journal = _state.TryLoad(_paths);
        Assert.NotNull(journal);
        Assert.Equal(PhaseStatus.Completed, journal.Phases[(int)WorkflowPhase.Specification].Status);
        Assert.Equal(PhaseStatus.Pending, journal.Phases[(int)WorkflowPhase.Simplifier].Status);
    }

    // --- Pause between subtasks ------------------------------------------------------------

    /// <summary>
    /// A subtask run whose index is already in place, so its first session is the first entry.
    /// Every entry gets its own description, so none of them is skipped as a failure.
    /// </summary>
    private SubtaskPaths ArrangeDecomposedSubtaskRun(params string[] titles)
    {
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();
        WriteSubtaskRunPrompt();
        WriteIndex(tracking, titles);

        foreach (var title in titles)
        {
            WriteDescription(tracking, title, $"Beschreibung {title}");
        }

        return tracking;
    }

    /// <summary>Waits until the loop has published the refreshed count of a finished subtask.</summary>
    private Task WaitForCompletedSubtasksAsync(int completed, CancellationToken cancellationToken) =>
        WaitUntil(() => SubtaskReports().Any(report => report.Completed >= completed), cancellationToken);

    [Fact]
    public async Task Pause_BetweenSubtasks_HoldsTheNextSubtaskUntilUnpaused()
    {
        // Arrange
        var tracking = ArrangeDecomposedSubtaskRun("ST-001-alpha", "ST-002-beta");
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var pause = new PhasePauseGate();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = CreateOrchestrator().RunAsync(
            CreateSubtaskRequest(terminal, new ManualPhaseSignal(), tracking.WorkflowDirectory) with { Pause = pause },
            cts.Token);
        await WaitForPasteAsync(terminal, 0, cts.Token);

        // Act - Pause while alpha runs; alpha then finishes on its own flag.
        pause.Pause();
        PublishSubtaskResult(tracking, "ST-001-alpha", "complete");
        await WaitForCompletedSubtasksAsync(1, cts.Token);
        await Task.Delay(500, cts.Token);

        // Assert - held: beta has not started and alpha's session has not been torn down.
        Assert.Single(terminal.StartedSessions);
        Assert.Equal(0, terminal.DisposeCount);
        Assert.False(run.IsCompleted);

        // Act - Play.
        pause.Unpause();

        // Assert
        await WaitForPasteAsync(terminal, 1, cts.Token);
        Assert.Contains("AUSFUEHRUNG <ST-002-beta>", terminal.Pasted[1], StringComparison.Ordinal);
        PublishSubtaskResult(tracking, "ST-002-beta", "complete");
        await run;
        Assert.Equal(2, terminal.StartedSessions.Count);
    }

    [Fact]
    public async Task Pause_ArmedBeforeASubtaskRun_NeverHoldsTheFirstSubtask()
    {
        // Arrange - 'Continue workflow' at phase 4 with the pause already armed.
        var tracking = ArrangeDecomposedSubtaskRun("ST-001-alpha", "ST-002-beta");
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var pause = new PhasePauseGate();
        pause.Pause();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        var run = CreateOrchestrator().RunAsync(
            CreateSubtaskRequest(terminal, new ManualPhaseSignal(), tracking.WorkflowDirectory) with { Pause = pause },
            cts.Token);

        // Assert - the first session is what the user just asked for, so it runs.
        await WaitForPasteAsync(terminal, 0, cts.Token);
        Assert.Contains("AUSFUEHRUNG <ST-001-alpha>", terminal.Pasted[0], StringComparison.Ordinal);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Pause_ManualCompletionWhileHeldBetweenSubtasks_EndsTheRun()
    {
        // Arrange - 'Task abschliessen' awaits the end of the run, so a hold that ignored the
        // manual signal would leave that button hanging until Play (spec D9).
        var tracking = ArrangeDecomposedSubtaskRun("ST-001-alpha", "ST-002-beta");
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var signal = new ManualPhaseSignal();
        var pause = new PhasePauseGate();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = CreateOrchestrator().RunAsync(
            CreateSubtaskRequest(terminal, signal, tracking.WorkflowDirectory) with { Pause = pause },
            cts.Token);
        await WaitForPasteAsync(terminal, 0, cts.Token);
        pause.Pause();
        PublishSubtaskResult(tracking, "ST-001-alpha", "complete");
        await WaitForCompletedSubtasksAsync(1, cts.Token);

        // Act
        signal.Signal();

        // Assert - the run ends while still paused, without starting beta.
        await run.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
        Assert.True(pause.IsPaused);
        Assert.Single(terminal.StartedSessions);
        Assert.False(File.Exists(tracking.SubtaskResultFile("ST-002-beta")));
    }

    [Fact]
    public async Task Pause_CancellationWhileHeldBetweenSubtasks_EndsTheRunAndDisposesTheSession()
    {
        // Arrange - the tab (or the application) is closed while the run is held between subtasks.
        var tracking = ArrangeDecomposedSubtaskRun("ST-001-alpha", "ST-002-beta");
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var pause = new PhasePauseGate();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = CreateOrchestrator().RunAsync(
            CreateSubtaskRequest(terminal, new ManualPhaseSignal(), tracking.WorkflowDirectory) with { Pause = pause },
            cts.Token);
        await WaitForPasteAsync(terminal, 0, cts.Token);
        pause.Pause();
        PublishSubtaskResult(tracking, "ST-001-alpha", "complete");
        await WaitForCompletedSubtasksAsync(1, cts.Token);

        // Act
        await cts.CancelAsync();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(terminal.DisposeCount >= 1);
        Assert.Single(terminal.StartedSessions);
    }

    [Fact]
    public async Task Pause_DuringTheLastSubtask_CompletesImplementationWithoutHolding()
    {
        // Arrange - nothing follows the last entry, so there is no boundary to hold at. Holding
        // anyway would keep a finished task Active until Play.
        var tracking = ArrangeDecomposedSubtaskRun("ST-001-alpha");
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var pause = new PhasePauseGate();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = CreateOrchestrator().RunAsync(
            CreateSubtaskRequest(terminal, new ManualPhaseSignal(), tracking.WorkflowDirectory) with { Pause = pause },
            cts.Token);
        await WaitForPasteAsync(terminal, 0, cts.Token);

        // Act
        pause.Pause();
        PublishSubtaskResult(tracking, "ST-001-alpha", "complete");

        // Assert
        await run.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
        Assert.Contains(
            _state.Recorded, r => r.Phase == WorkflowPhase.Implementation && r.Status == PhaseStatus.Completed);
    }

    [Fact]
    public async Task Pause_DuringDecomposition_HoldsTheFirstSubtask()
    {
        // Arrange - no index yet, so the run starts with the decomposition session.
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();
        WriteSubtaskRunPrompt();
        WriteDescription(tracking, "ST-001-alpha", "Beschreibung ST-001-alpha");
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var pause = new PhasePauseGate();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = CreateOrchestrator().RunAsync(
            CreateSubtaskRequest(terminal, new ManualPhaseSignal(), tracking.WorkflowDirectory) with { Pause = pause },
            cts.Token);
        await WaitForPasteAsync(terminal, 0, cts.Token);
        Assert.Contains("ZERLEGUNG", terminal.Pasted[0], StringComparison.Ordinal);

        // Act - Pause while decomposition runs; it then publishes the index.
        pause.Pause();
        WriteIndex(tracking, "ST-001-alpha");
        await Task.Delay(500, cts.Token);

        // Assert - held before the first subtask.
        Assert.Single(terminal.StartedSessions);
        Assert.False(run.IsCompleted);

        // Act - Play.
        pause.Unpause();

        // Assert
        await WaitForPasteAsync(terminal, 1, cts.Token);
        Assert.Contains("AUSFUEHRUNG <ST-001-alpha>", terminal.Pasted[1], StringComparison.Ordinal);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    // --- Unattended sessions: launcher flags and the stall watchdog (B6) ----------------------

    private const string UnattendedLauncherPrefix =
        "try { yo --disallowedTools AskUserQuestion --append-system-prompt-file \"";

    /// <summary>
    /// A clock that follows real time plus an offset the test advances, and whose delays are short
    /// real waits. Real time underneath keeps it comparable with the terminal's own output stamps.
    /// </summary>
    private sealed class SteppedClock : IDesktopJobClock
    {
        private long _offsetTicks;

        public DateTimeOffset UtcNow =>
            DateTimeOffset.UtcNow + TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));

        public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);

        public Task DelayAsync(TimeSpan interval, CancellationToken cancellationToken) =>
            Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
    }

    private WorkflowOrchestrator CreateUnattendedOrchestrator(
        StallPolicy? stall = null,
        IDesktopJobClock? clock = null,
        bool requireDecisionApproval = true,
        string? noHumanPromptPath = null) => new(
        new PromptTemplateService(_promptDir),
        new AutoAnswerService(_rulesPath, overridePath: null),
        new ArtifactWatcherFactory(),
        _state,
        TimeSpan.FromMilliseconds(20),
        TimeSpan.FromMilliseconds(40),
        stall: stall,
        clock: clock,
        requireDecisionApproval: requireDecisionApproval,
        noHumanPromptPath: noHumanPromptPath);

    /// <summary>The system prompt file the launcher line hands to the CLI.</summary>
    private static string AppendedPromptFile(string launcherLine)
    {
        var match = Regex.Match(launcherLine, "--append-system-prompt-file \"(?<path>[^\"]+)\"");
        Assert.True(match.Success, $"No --append-system-prompt-file argument in: {launcherLine}");

        return match.Groups["path"].Value;
    }

    private static void AssertUnattendedLauncher(string launcherLine)
    {
        Assert.Contains(UnattendedLauncherPrefix, launcherLine, StringComparison.Ordinal);

        var path = AppendedPromptFile(launcherLine);
        Assert.True(Path.IsPathFullyQualified(path), path);
        Assert.Equal("no_human.md", Path.GetFileName(path));
        Assert.True(File.Exists(path), path);

        // Everything between the launcher and the closing brace reaches cmd through yo.bat.
        var launcher = launcherLine[launcherLine.IndexOf("yo ", StringComparison.Ordinal)..launcherLine.IndexOf(" }", StringComparison.Ordinal)];
        Assert.DoesNotContain(launcher, c => c is '&' or '|' or '<' or '>' or '^' or '%');
    }

    [Fact]
    public async Task Unattended_SubtaskAndDecompositionSessions_DisallowQuestionsAndAppendTheNoHumanPrompt()
    {
        // Arrange - no index yet, so the run decomposes first and then runs the one entry.
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();
        WriteSubtaskRunPrompt();
        WriteDescription(tracking, "ST-001-alpha", "Beschreibung ST-001-alpha");
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        var run = CreateUnattendedOrchestrator(requireDecisionApproval: false).RunAsync(
            CreateSubtaskRequest(terminal, new ManualPhaseSignal(), tracking.WorkflowDirectory), cts.Token);
        await WaitForPasteAsync(terminal, 0, cts.Token);
        var decompositionLauncher = terminal.Sent[1];
        WriteIndex(tracking, "ST-001-alpha");
        await WaitForPasteAsync(terminal, 1, cts.Token);
        var subtaskLauncher = terminal.Sent.Last(text => text.Contains("WF-RUN-", StringComparison.Ordinal));
        PublishSubtaskResult(tracking, "ST-001-alpha", "complete");
        await run;

        // Assert
        Assert.Contains("ZERLEGUNG", terminal.Pasted[0], StringComparison.Ordinal);
        AssertUnattendedLauncher(decompositionLauncher);
        Assert.Contains("AUSFUEHRUNG <ST-001-alpha>", terminal.Pasted[1], StringComparison.Ordinal);
        AssertUnattendedLauncher(subtaskLauncher);
    }

    [Fact]
    public async Task Unattended_PhaseSession_KeepsThePlainLauncher()
    {
        // Arrange - a phase session has a human at the keyboard; only the unattended ones change.
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var request = CreateRequest(terminal, new ManualPhaseSignal()) with { StartPhase = WorkflowPhase.Implementation };

        // Act
        var run = CreateUnattendedOrchestrator().RunAsync(request, cts.Token);
        await WaitForPasteAsync(terminal, 0, cts.Token);
        WriteArtifacts(_paths.DoneAbsolute);
        await run;

        // Assert
        Assert.Contains("try { yo } finally {", terminal.Sent[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unattended_MissingNoHumanPrompt_FailsFastBeforeAnySessionStarts()
    {
        // Arrange
        var tracking = ArrangeDecomposedSubtaskRun("ST-001-alpha");
        var missing = Path.Combine(_root, "fehlt", "no_human.md");
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        var error = await Assert.ThrowsAsync<PromptTemplateException>(() =>
            CreateUnattendedOrchestrator(requireDecisionApproval: false, noHumanPromptPath: missing).RunAsync(
                CreateSubtaskRequest(terminal, new ManualPhaseSignal(), tracking.WorkflowDirectory), cts.Token));

        // Assert
        Assert.Contains(missing, error.Message, StringComparison.Ordinal);
        Assert.Empty(terminal.StartedSessions);
    }

    private static readonly StallPolicy QuarterHourStall = new(TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(30));

    [Fact]
    public async Task Watchdog_FirstStall_SendsEscapeAndTheNudgeAndKeepsTheSession()
    {
        // Arrange
        var tracking = ArrangeDecomposedSubtaskRun("ST-001-alpha");
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var lastWriteBeforePaste = new List<string?>();
        terminal.OnPaste = () => lastWriteBeforePaste.Add(terminal.Sent.LastOrDefault());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = CreateUnattendedOrchestrator(QuarterHourStall, new SteppedClock(), requireDecisionApproval: false)
            .RunAsync(CreateSubtaskRequest(terminal, new ManualPhaseSignal(), tracking.WorkflowDirectory), cts.Token);
        await WaitForPasteAsync(terminal, 0, cts.Token);

        // Act - sixteen silent minutes.
        terminal.BackdateOutput(TimeSpan.FromMinutes(16));
        await WaitForPasteAsync(terminal, 1, cts.Token);

        // Assert - Escape first, then the nudge through the prompt path; the session lives on.
        Assert.Equal("\u001b", lastWriteBeforePaste[1]);
        Assert.StartsWith("Es gibt keinen Menschen. Beende die Subtask jetzt", terminal.Pasted[1], StringComparison.Ordinal);
        Assert.Equal(0, terminal.DisposeCount);
        Assert.False(run.IsCompleted);

        PublishSubtaskResult(tracking, "ST-001-alpha", "complete");
        await run;
        Assert.Equal(SubtaskStatus.Complete, SubtaskLedger.TryRead(tracking)!.States.Single().Status);
        Assert.Equal(2, terminal.Pasted.Count);
    }

    [Fact]
    public async Task Watchdog_SecondStall_KillsTheSessionBeforeTheApplicationWritesTheFailedStatus()
    {
        // Arrange
        var tracking = ArrangeDecomposedSubtaskRun("ST-001-alpha");
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var clock = new SteppedClock();
        var onDisk = new List<(bool Status, bool Flag)>();
        terminal.OnStopSession = () => onDisk.Add((
            File.Exists(tracking.SubtaskStatusFile("ST-001-alpha")),
            File.Exists(tracking.SubtaskResultFile("ST-001-alpha"))));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = CreateUnattendedOrchestrator(QuarterHourStall, clock, requireDecisionApproval: false)
            .RunAsync(CreateSubtaskRequest(terminal, new ManualPhaseSignal(), tracking.WorkflowDirectory), cts.Token);
        await WaitForPasteAsync(terminal, 0, cts.Token);
        terminal.BackdateOutput(TimeSpan.FromMinutes(16));
        await WaitForPasteAsync(terminal, 1, cts.Token);

        // Act - the nudge changed nothing: another sixteen silent minutes.
        clock.Advance(TimeSpan.FromMinutes(16));
        await run;

        // Assert - the confirmed stop came first, with neither file on disk yet.
        Assert.NotEmpty(onDisk);
        Assert.Equal((false, false), onDisk[0]);

        using var status = JsonDocument.Parse(await File.ReadAllTextAsync(tracking.SubtaskStatusFile("ST-001-alpha"), cts.Token));
        Assert.Equal("ST-001-alpha", status.RootElement.GetProperty("subtask").GetString());
        Assert.Equal("failed", status.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "stalled: keine Terminalausgabe seit 15 min",
            status.RootElement.GetProperty("failreason").GetString());
        Assert.False(status.RootElement.GetProperty("testsPassed").GetBoolean());
        Assert.True(new FileInfo(tracking.SubtaskResultFile("ST-001-alpha")).Length > 0);
        Assert.Empty(Directory.GetFiles(tracking.SubtaskDirectory("ST-001-alpha"), "*.tmp"));

        var state = SubtaskLedger.TryRead(tracking)!.States.Single();
        Assert.Equal(SubtaskStatus.Failed, state.Status);
        Assert.StartsWith("stalled:", state.FailReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Watchdog_ASessionThatKeepsProducingOutput_IsNeverNudged()
    {
        // Arrange - the real clock and a short threshold, with output well inside it.
        var tracking = ArrangeDecomposedSubtaskRun("ST-001-alpha");
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var stall = new StallPolicy(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(20));
        var run = CreateUnattendedOrchestrator(stall, requireDecisionApproval: false)
            .RunAsync(CreateSubtaskRequest(terminal, new ManualPhaseSignal(), tracking.WorkflowDirectory), cts.Token);
        await WaitForPasteAsync(terminal, 0, cts.Token);

        // Act - two seconds of steady output.
        for (var chunk = 0; chunk < 40; chunk++)
        {
            terminal.EmitOutput();
            await Task.Delay(50, cts.Token);
        }

        // Assert
        Assert.Single(terminal.Pasted);
        Assert.DoesNotContain("\u001b", terminal.Sent);
        Assert.Equal(0, terminal.DisposeCount);

        PublishSubtaskResult(tracking, "ST-001-alpha", "complete");
        await run;
    }

    [Fact]
    public async Task Watchdog_ASessionWithoutAPolicy_IsNeverNudgedOrKilled()
    {
        // Arrange - phase sessions get no stall policy, however short the orchestrator's is.
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var stall = new StallPolicy(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(10));
        var request = CreateRequest(terminal, new ManualPhaseSignal()) with { StartPhase = WorkflowPhase.Implementation };
        var run = CreateUnattendedOrchestrator(stall).RunAsync(request, cts.Token);
        await WaitForPasteAsync(terminal, 0, cts.Token);

        // Act
        terminal.BackdateOutput(TimeSpan.FromHours(1));
        await Task.Delay(500, cts.Token);

        // Assert
        Assert.Single(terminal.Pasted);
        Assert.DoesNotContain("\u001b", terminal.Sent);
        Assert.Equal(0, terminal.DisposeCount);
        Assert.False(run.IsCompleted);

        WriteArtifacts(_paths.DoneAbsolute);
        await run;
    }

    // --- Decision pre-flight with hash-bound approval (B7) -------------------------------------

    private const string DecisionsPromptFile = "create_decisions.md";

    /// <summary>The hold's status line, naming the first eight hex digits of the decisions shown.</summary>
    private static string DecisionsAwaitApprovalMessage(SubtaskPaths tracking) =>
        $"Entscheidungen prüfen (decisions.md {Sha256Of(DecisionsFile(tracking))[..8]}…): freigeben mit Play";

    // Every token of the pre-flight's set that create_decisions.md uses, so a dropped one throws.
    private void WriteDecisionsPrompt() => File.WriteAllText(
        Path.Combine(_promptDir, DecisionsPromptFile),
        "ENTSCHEIDUNGEN {task_path} | {subtask_path} | {spec_path} | {plan_path}");

    private static string DecisionsFile(SubtaskPaths tracking) => Path.Combine(tracking.TaskDirectory, "decisions.md");

    /// <summary>The approval in application-owned storage, outside the task folder.</summary>
    private static string ApprovalFile(SubtaskPaths tracking) => DecisionApproval.ApprovalFile(tracking);

    /// <summary>Where the approval used to live - inside the task folder, where an agent can write.</summary>
    private static string TaskFolderApprovalFile(SubtaskPaths tracking) =>
        Path.Combine(tracking.TaskDirectory, "decisions.approved.json");

    private static string Sha256Of(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void WriteDecisions(SubtaskPaths tracking, string content) =>
        File.WriteAllText(DecisionsFile(tracking), content);

    /// <summary>An approval as the application writes it, bound to the files on disk right now.</summary>
    private static void WriteApproval(SubtaskPaths tracking, string taskTitle, string? path = null)
    {
        path ??= ApprovalFile(tracking);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(new
            {
                decisionsSha256 = Sha256Of(DecisionsFile(tracking)),
                indexSha256 = Sha256Of(tracking.ResultAbsolute),
                taskTitle,
                taskPath = tracking.TaskDirectory,
                approvedUtc = DateTimeOffset.UtcNow,
            }));
    }

    private static void AssertApprovalMatchesTheFiles(SubtaskPaths tracking, string taskTitle)
    {
        using var approval = JsonDocument.Parse(File.ReadAllText(ApprovalFile(tracking)));
        var root = approval.RootElement;

        Assert.Equal(Sha256Of(DecisionsFile(tracking)), root.GetProperty("decisionsSha256").GetString(), ignoreCase: true);
        Assert.Equal(Sha256Of(tracking.ResultAbsolute), root.GetProperty("indexSha256").GetString(), ignoreCase: true);
        Assert.Equal(taskTitle, root.GetProperty("taskTitle").GetString());
        Assert.True(root.GetProperty("approvedUtc").TryGetDateTimeOffset(out _));
    }

    private (Task Run, FakeTerminalController Terminal, PhasePauseGate Pause, List<string?> Status) StartPreflightRun(
        SubtaskPaths tracking,
        CancellationToken cancellationToken,
        ManualPhaseSignal? signal = null)
    {
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var pause = new PhasePauseGate();
        var status = new List<string?>();
        var request = CreateSubtaskRequest(terminal, signal ?? new ManualPhaseSignal(), tracking.WorkflowDirectory) with
        {
            Pause = pause,
            LauncherStatus = new InlineProgress<string?>(message =>
            {
                lock (status)
                {
                    status.Add(message);
                }
            }),
        };

        return (CreateUnattendedOrchestrator().RunAsync(request, cancellationToken), terminal, pause, status);
    }

    [Fact]
    public async Task Preflight_MissingDecisions_RunsTheDecisionsSessionBeforeTheFirstSubtaskAndHoldsForApproval()
    {
        // Arrange
        var tracking = ArrangeDecomposedSubtaskRun("ST-001-alpha");
        WriteDecisionsPrompt();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        var (run, terminal, pause, status) = StartPreflightRun(tracking, cts.Token);
        await WaitForPasteAsync(terminal, 0, cts.Token);

        // Assert - the pre-flight session comes first, unattended, aimed at the task folder.
        Assert.Contains("ENTSCHEIDUNGEN", terminal.Pasted[0], StringComparison.Ordinal);
        Assert.Contains(tracking.TaskDirectory, terminal.Pasted[0], StringComparison.Ordinal);
        AssertUnattendedLauncher(terminal.Sent[1]);

        // Act - the session writes decisions.md and ends; the run holds for approval.
        WriteDecisions(tracking, "## D-001\n- policy: use-default\n");
        await WaitUntil(() => pause.IsPaused, cts.Token);
        await Task.Delay(300, cts.Token);

        // Assert
        Assert.Single(terminal.StartedSessions);
        Assert.False(File.Exists(ApprovalFile(tracking)));
        lock (status)
        {
            Assert.Contains(DecisionsAwaitApprovalMessage(tracking), status);
        }

        // Act - Play approves.
        pause.Unpause();
        await WaitForPasteAsync(terminal, 1, cts.Token);

        // Assert
        Assert.Contains("AUSFUEHRUNG <ST-001-alpha>", terminal.Pasted[1], StringComparison.Ordinal);
        AssertApprovalMatchesTheFiles(tracking, _paths.TaskName);
        PublishSubtaskResult(tracking, "ST-001-alpha", "complete");
        await run;
    }

    [Fact]
    public async Task Preflight_ValidApproval_StartsTheFirstSubtaskWithoutHolding()
    {
        // Arrange
        var tracking = ArrangeDecomposedSubtaskRun("ST-001-alpha");
        WriteDecisionsPrompt();
        WriteDecisions(tracking, "## D-001\n- policy: use-default\n");
        WriteApproval(tracking, _paths.TaskName);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        var (run, terminal, pause, _) = StartPreflightRun(tracking, cts.Token);
        await WaitForPasteAsync(terminal, 0, cts.Token);

        // Assert
        Assert.Contains("AUSFUEHRUNG <ST-001-alpha>", terminal.Pasted[0], StringComparison.Ordinal);
        Assert.False(pause.IsPaused);
        PublishSubtaskResult(tracking, "ST-001-alpha", "complete");
        await run;
        Assert.Single(terminal.StartedSessions);
    }

    [Fact]
    public async Task Preflight_DecisionsEditedAfterApproval_HoldsBeforeTheNextSubtaskSession()
    {
        // Arrange
        var tracking = ArrangeDecomposedSubtaskRun("ST-001-alpha", "ST-002-beta");
        WriteDecisionsPrompt();
        WriteDecisions(tracking, "## D-001\n- policy: use-default\n");
        WriteApproval(tracking, _paths.TaskName);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (run, terminal, pause, _) = StartPreflightRun(tracking, cts.Token);
        await WaitForPasteAsync(terminal, 0, cts.Token);

        // Act - decisions.md changes while alpha runs.
        await File.AppendAllTextAsync(DecisionsFile(tracking), "- answer: nachträglich geändert\n", cts.Token);
        PublishSubtaskResult(tracking, "ST-001-alpha", "complete");
        await WaitUntil(() => pause.IsPaused, cts.Token);
        await Task.Delay(300, cts.Token);

        // Assert - beta has not started.
        Assert.Single(terminal.StartedSessions);

        // Act - Play re-approves the edited file.
        pause.Unpause();
        await WaitForPasteAsync(terminal, 1, cts.Token);

        // Assert
        Assert.Contains("AUSFUEHRUNG <ST-002-beta>", terminal.Pasted[1], StringComparison.Ordinal);
        AssertApprovalMatchesTheFiles(tracking, _paths.TaskName);
        PublishSubtaskResult(tracking, "ST-002-beta", "complete");
        await run;
    }

    [Fact]
    public async Task Preflight_RegeneratedDecisionsWithAnOldApproval_HoldsBeforeTheFirstSubtask()
    {
        // Arrange - the approval was given for an earlier version of decisions.md.
        var tracking = ArrangeDecomposedSubtaskRun("ST-001-alpha");
        WriteDecisionsPrompt();
        WriteDecisions(tracking, "## D-001\n- policy: use-default\n");
        WriteApproval(tracking, _paths.TaskName);
        WriteDecisions(tracking, "## D-001\n- policy: fail\n");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        var (run, terminal, pause, _) = StartPreflightRun(tracking, cts.Token);
        await WaitUntil(() => pause.IsPaused, cts.Token);
        await Task.Delay(300, cts.Token);

        // Assert - no session at all: the file exists, so nothing is regenerated either.
        Assert.Empty(terminal.StartedSessions);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Preflight_DecompositionInThisRun_RegeneratesTheDecisionsAndDropsTheOldApproval()
    {
        // Arrange - no index, but decisions.md and an approval from an earlier decomposition.
        var tracking = new SubtaskPaths(CreateTrackingRepository(withTemplateMarker: true), _paths.TaskName);
        WriteDecompositionPrompt();
        WriteSubtaskRunPrompt();
        WriteDecisionsPrompt();
        WriteIndex(tracking, "ST-000-alt");
        WriteDecisions(tracking, "## D-001\n- policy: use-default\n");
        WriteApproval(tracking, _paths.TaskName);
        File.Delete(tracking.ResultAbsolute);
        WriteDescription(tracking, "ST-001-alpha", "Beschreibung ST-001-alpha");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (run, terminal, pause, _) = StartPreflightRun(tracking, cts.Token);
        await WaitForPasteAsync(terminal, 0, cts.Token);
        Assert.Contains("ZERLEGUNG", terminal.Pasted[0], StringComparison.Ordinal);

        // Act - decomposition publishes the index.
        WriteIndex(tracking, "ST-001-alpha");
        await WaitForPasteAsync(terminal, 1, cts.Token);

        // Assert - the decisions session runs, and neither old file survives into it.
        Assert.Contains("ENTSCHEIDUNGEN", terminal.Pasted[1], StringComparison.Ordinal);
        Assert.False(File.Exists(ApprovalFile(tracking)));
        Assert.False(File.Exists(DecisionsFile(tracking)));

        // Act - the session writes the new decisions.
        WriteDecisions(tracking, "## D-001\n- policy: desktop-job\n");
        await WaitUntil(() => pause.IsPaused, cts.Token);
        await Task.Delay(300, cts.Token);

        // Assert - held before the first subtask.
        Assert.Equal(2, terminal.StartedSessions.Count);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Preflight_ApprovalCopiedFromAnotherTask_HoldsAndPlayWritesThisTasksApproval()
    {
        // Arrange - both hashes match, only the task title gives the copy away.
        var tracking = ArrangeDecomposedSubtaskRun("ST-001-alpha");
        WriteDecisionsPrompt();
        WriteDecisions(tracking, "## D-001\n- policy: use-default\n");
        WriteApproval(tracking, "anderer-task");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        var (run, terminal, pause, _) = StartPreflightRun(tracking, cts.Token);
        await WaitUntil(() => pause.IsPaused, cts.Token);
        await Task.Delay(300, cts.Token);

        // Assert
        Assert.Empty(terminal.StartedSessions);

        // Act - Play.
        pause.Unpause();
        await WaitForPasteAsync(terminal, 0, cts.Token);

        // Assert
        Assert.Contains("AUSFUEHRUNG <ST-001-alpha>", terminal.Pasted[0], StringComparison.Ordinal);
        AssertApprovalMatchesTheFiles(tracking, _paths.TaskName);
        PublishSubtaskResult(tracking, "ST-001-alpha", "complete");
        await run;
    }

    [Fact]
    public async Task Preflight_HandWrittenDecisionsWithoutApproval_HoldsWithoutRegenerating()
    {
        // Arrange
        const string HandWritten = "## D-001\n- policy: use-default\n- answer: von Hand\n";
        var tracking = ArrangeDecomposedSubtaskRun("ST-001-alpha");
        WriteDecisionsPrompt();
        WriteDecisions(tracking, HandWritten);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        var (run, terminal, pause, status) = StartPreflightRun(tracking, cts.Token);
        await WaitUntil(() => pause.IsPaused, cts.Token);
        await Task.Delay(300, cts.Token);

        // Assert
        Assert.Empty(terminal.StartedSessions);
        Assert.Equal(HandWritten, await File.ReadAllTextAsync(DecisionsFile(tracking), cts.Token));
        lock (status)
        {
            Assert.Contains(DecisionsAwaitApprovalMessage(tracking), status);
        }

        // Act - Play.
        pause.Unpause();
        await WaitForPasteAsync(terminal, 0, cts.Token);

        // Assert
        Assert.Contains("AUSFUEHRUNG <ST-001-alpha>", terminal.Pasted[0], StringComparison.Ordinal);
        PublishSubtaskResult(tracking, "ST-001-alpha", "complete");
        await run;
    }

    [Fact]
    public async Task Preflight_ManualCompletionWhileHeldForApproval_EndsTheRunWithoutApproving()
    {
        // Arrange
        var tracking = ArrangeDecomposedSubtaskRun("ST-001-alpha");
        WriteDecisionsPrompt();
        WriteDecisions(tracking, "## D-001\n- policy: use-default\n");
        var signal = new ManualPhaseSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (run, terminal, pause, _) = StartPreflightRun(tracking, cts.Token, signal);
        await WaitUntil(() => pause.IsPaused, cts.Token);

        // Act
        signal.Signal();

        // Assert
        await run.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
        Assert.Empty(terminal.StartedSessions);
        Assert.False(File.Exists(ApprovalFile(tracking)));
    }

    [Fact]
    public async Task Preflight_AnApprovalWrittenIntoTheTaskFolder_IsIgnored()
    {
        // Arrange - a perfect approval, but where an agent can write it.
        var tracking = ArrangeDecomposedSubtaskRun("ST-001-alpha");
        WriteDecisionsPrompt();
        WriteDecisions(tracking, "## D-001\n- policy: use-default\n");
        WriteApproval(tracking, _paths.TaskName, TaskFolderApprovalFile(tracking));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        var (run, terminal, pause, _) = StartPreflightRun(tracking, cts.Token);
        await WaitUntil(() => pause.IsPaused, cts.Token);
        await Task.Delay(300, cts.Token);

        // Assert
        Assert.Empty(terminal.StartedSessions);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Preflight_DecisionsChangedWhileTheHoldWasShown_PlayApprovesNothingAndHoldsAgain()
    {
        // Arrange
        var tracking = ArrangeDecomposedSubtaskRun("ST-001-alpha");
        WriteDecisionsPrompt();
        WriteDecisions(tracking, "## D-001\n- policy: use-default\n");
        var shown = DecisionsAwaitApprovalMessage(tracking);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (run, terminal, pause, status) = StartPreflightRun(tracking, cts.Token);
        await WaitUntil(() => pause.IsPaused, cts.Token);
        lock (status)
        {
            Assert.Contains(shown, status);
        }

        // Act - decisions.md changes after the user saw it, then Play.
        WriteDecisions(tracking, "## D-001\n- policy: fail\n");
        pause.Unpause();
        await WaitUntil(
            () =>
            {
                lock (status)
                {
                    return status.Any(message => message?.StartsWith("decisions.md wurde geändert – erneut prüfen", StringComparison.Ordinal) == true);
                }
            },
            cts.Token);
        await WaitUntil(() => pause.IsPaused, cts.Token);

        // Assert - nothing approved, no session; the new hold names the new hash.
        Assert.False(File.Exists(ApprovalFile(tracking)));
        Assert.Empty(terminal.StartedSessions);
        lock (status)
        {
            Assert.Contains(status, message => message?.Contains(DecisionsAwaitApprovalMessage(tracking), StringComparison.Ordinal) == true);
        }

        // Act - Play on the version now shown approves it.
        pause.Unpause();
        await WaitForPasteAsync(terminal, 0, cts.Token);

        // Assert
        AssertApprovalMatchesTheFiles(tracking, _paths.TaskName);
        PublishSubtaskResult(tracking, "ST-001-alpha", "complete");
        await run;
    }

    // --- Review findings: confirmed stops, the gate and desktop jobs next to live sessions -----

    /// <summary>
    /// Writes a one-step desktopjob.json and its script for <paramref name="title"/> under a new request id, as a
    /// session that defers does. The script file is always desktopjob.serie.ps1; <paramref name="script"/> is its
    /// content and <paramref name="stepId"/> the step's id.
    /// </summary>
    private void WriteDesktopJobRequest(
        SubtaskPaths tracking,
        string title,
        int timeoutMinutes = 45,
        string script = "Write-Output 'serie'",
        string stepId = "serie")
    {
        var directory = tracking.SubtaskDirectory(title);
        File.WriteAllText(Path.Combine(directory, "desktopjob.serie.ps1"), script);
        File.WriteAllText(
            Path.Combine(directory, DesktopJobFiles.RequestFileName),
            JsonSerializer.Serialize(new
            {
                version = 2,
                requestId = Guid.NewGuid(),
                idleMinutes = 15,
                steps = new[]
                {
                    new { id = stepId, script = "desktopjob.serie.ps1", timeoutMinutes, marker = Path.Combine(_root, "serie.marker") },
                },
            }));
    }

    [Fact]
    public async Task DesktopJob_AfterASessionDeferred_ItsLiveTerminalSessionIsStoppedAndConfirmedBeforeTheJobStarts()
    {
        // Arrange
        var tracking = ArrangeDecomposedSubtaskRun("ST-001-alpha");
        var starter = new FakeDesktopJobProcessStarter { MarkerFor = _ => Path.Combine(_root, "serie.marker") };
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        var stopsAtJobStart = new List<int>();
        starter.OnStart = _ => stopsAtJobStart.Add(terminal.StopCount);
        var runner = new DesktopJobRunner(new FakeUserActivityMonitor(), new FakeCpuLoadProbe(), starter, new FakeDesktopJobClock());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = CreateDesktopJobOrchestrator(runner).RunAsync(
            CreateSubtaskRequest(terminal, new ManualPhaseSignal(), tracking.WorkflowDirectory), cts.Token);
        await WaitForPasteAsync(terminal, 0, cts.Token);

        // Act - the session defers to a desktop job and publishes its flag; Claude is still alive.
        WriteDesktopJobRequest(tracking, "ST-001-alpha");
        WriteStatus(tracking, "ST-001-alpha", """{"status":"deferred","failreason":"Desktop-Job"}""");
        PublishSubtaskResult(tracking, "ST-001-alpha", status: null);
        await WaitForPasteAsync(terminal, 1, cts.Token);

        // Assert - one confirmed stop, before the job's process started.
        Assert.Equal([1], stopsAtJobStart);

        PublishSubtaskResult(tracking, "ST-001-alpha", "complete");
        await run;
    }

    [Fact]
    public async Task DesktopJob_AnUnconfirmedStopOfTheLiveSession_StartsNoJobAndEndsTheRunWithAnError()
    {
        // Arrange
        var tracking = ArrangeDecomposedSubtaskRun("ST-001-alpha");
        var starter = new FakeDesktopJobProcessStarter { MarkerFor = _ => Path.Combine(_root, "serie.marker") };
        var terminal = new FakeTerminalController { StopConfirms = false };
        terminal.ReadyGate.SetResult();
        var runner = new DesktopJobRunner(new FakeUserActivityMonitor(), new FakeCpuLoadProbe(), starter, new FakeDesktopJobClock());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = CreateDesktopJobOrchestrator(runner).RunAsync(
            CreateSubtaskRequest(terminal, new ManualPhaseSignal(), tracking.WorkflowDirectory), cts.Token);
        await WaitForPasteAsync(terminal, 0, cts.Token);

        // Act
        WriteDesktopJobRequest(tracking, "ST-001-alpha");
        WriteStatus(tracking, "ST-001-alpha", """{"status":"deferred","failreason":"Desktop-Job"}""");
        PublishSubtaskResult(tracking, "ST-001-alpha", status: null);

        // Assert
        var error = await Assert.ThrowsAsync<SubtaskConfigurationException>(() => run);
        Assert.Contains("ließ sich nicht beenden", error.Message, StringComparison.Ordinal);
        Assert.Empty(starter.Launches);
    }

    [Fact]
    public async Task Watchdog_SecondStallWithAnUnconfirmedStop_WritesNothingAndEndsTheRunWithAnError()
    {
        // Arrange
        var tracking = ArrangeDecomposedSubtaskRun("ST-001-alpha");
        var terminal = new FakeTerminalController { StopConfirms = false };
        terminal.ReadyGate.SetResult();
        var clock = new SteppedClock();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = CreateUnattendedOrchestrator(QuarterHourStall, clock, requireDecisionApproval: false)
            .RunAsync(CreateSubtaskRequest(terminal, new ManualPhaseSignal(), tracking.WorkflowDirectory), cts.Token);
        await WaitForPasteAsync(terminal, 0, cts.Token);
        terminal.BackdateOutput(TimeSpan.FromMinutes(16));
        await WaitForPasteAsync(terminal, 1, cts.Token);

        // Act
        clock.Advance(TimeSpan.FromMinutes(16));

        // Assert
        var error = await Assert.ThrowsAsync<SubtaskConfigurationException>(() => run);
        Assert.Equal("Session ließ sich nicht beenden; Status nicht geschrieben.", error.Message);
        Assert.Equal(1, terminal.StopCount);
        Assert.False(File.Exists(tracking.SubtaskStatusFile("ST-001-alpha")));
        Assert.False(File.Exists(tracking.SubtaskResultFile("ST-001-alpha")));
    }

    [Fact]
    public async Task ExecutionGate_APhaseSessionWithAHumanInTheLoop_DoesNotHoldTheGate()
    {
        // Arrange
        var gate = new ExecutionGate();
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var orchestrator = CreateDesktopJobOrchestrator(
            new DesktopJobRunner(new FakeUserActivityMonitor(), new FakeCpuLoadProbe(), new FakeDesktopJobProcessStarter()),
            gate);
        var run = orchestrator.RunAsync(
            CreateRequest(terminal, new ManualPhaseSignal()) with { StartPhase = WorkflowPhase.Implementation },
            cts.Token);
        await WaitForPasteAsync(terminal, 0, cts.Token);

        // Act
        var exclusive = gate.AcquireExclusiveAsync(cts.Token);

        // Assert
        Assert.True(exclusive.IsCompletedSuccessfully);
        (await exclusive).Dispose();

        WriteArtifacts(_paths.DoneAbsolute);
        await run;
    }

    [Fact]
    public async Task ExecutionGate_AnUnattendedSubtaskSession_HoldsTheGateUntilItEnds()
    {
        // Arrange
        var tracking = ArrangeDecomposedSubtaskRun("ST-001-alpha");
        var gate = new ExecutionGate();
        var terminal = new FakeTerminalController();
        terminal.ReadyGate.SetResult();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var orchestrator = CreateDesktopJobOrchestrator(
            new DesktopJobRunner(new FakeUserActivityMonitor(), new FakeCpuLoadProbe(), new FakeDesktopJobProcessStarter()),
            gate);
        var run = orchestrator.RunAsync(
            CreateSubtaskRequest(terminal, new ManualPhaseSignal(), tracking.WorkflowDirectory), cts.Token);
        await WaitForPasteAsync(terminal, 0, cts.Token);

        // Act
        var exclusive = gate.AcquireExclusiveAsync(cts.Token);
        await Task.Delay(200, cts.Token);

        // Assert
        Assert.False(exclusive.IsCompleted);

        PublishSubtaskResult(tracking, "ST-001-alpha", "complete");
        await run;
        (await exclusive.WaitAsync(TimeSpan.FromSeconds(10), cts.Token)).Dispose();
    }
}
