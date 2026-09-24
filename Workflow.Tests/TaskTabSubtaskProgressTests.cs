using System.ComponentModel;
using System.IO;
using System.Windows.Threading;
using Workflow.Models;
using Workflow.Services;
using Workflow.Terminal;
using Workflow.Tests.Fakes;
using Workflow.ViewModels;

namespace Workflow.Tests;

/// <remarks>
/// <para>
/// Task 6.4: the tab creates the <see cref="IProgress{T}"/> sink it hands to the run and applies
/// every published payload to the indicator it owns, on the UI thread, while the run continues
/// (requirements 3.2 and 3.7). It also decides what the indicator shows at run entry: reset before
/// phase 4 (requirement 3.4), and the disk-derived counts kept for a run that starts <em>at</em>
/// phase 4 (requirement 4.5).
/// </para>
/// <para>
/// The async tests drive the real sink taken off the captured <see cref="WorkflowRunRequest"/> and
/// report from a pool thread, exactly as <c>WorkflowOrchestrator</c> does. Asserting synchronously
/// after <see cref="IProgress{T}.Report"/> would prove nothing: <see cref="Progress{T}"/> posts to
/// the synchronization context captured at construction instead of invoking inline, so the
/// assertion has to await the marshalled result. That posting is also the thing under test - a sink
/// built anywhere but the UI thread would run the callback on the reporting thread, which the
/// dispatcher-affinity counter below catches.
/// </para>
/// <para>
/// Every payload is asymmetric in all three counters and the failure reasons are textually distinct
/// from the titles they belong to, so a mutant that swaps two counters, or that renders a title
/// where a reason belongs, fails instead of passing.
/// </para>
/// </remarks>
public sealed class TaskTabSubtaskProgressTests : IDisposable
{
    private const string TaskName = "alpha";

    private static readonly string[] SixEntries =
    [
        "ST-001-lesen",
        "ST-002-pruefen",
        "ST-003-schreiben",
        "ST-004-testen",
        "ST-005-melden",
        "ST-006-abschliessen",
    ];

    private readonly string _root;
    private readonly string _workspace;
    private readonly string _tracking;
    private readonly SettingsService _settings;

    public TaskTabSubtaskProgressTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wf-tab64-" + Guid.NewGuid().ToString("N"));
        _workspace = Path.Combine(_root, "arbeit");
        _tracking = Path.Combine(_root, "workflows");

        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(Path.Combine(_tracking, SubtaskPaths.TemplateFolderName));

        _settings = new SettingsService(Path.Combine(_root, "settings.json"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // CA2000 cannot trace the disposal: TerminalViewModel's ownership passes into the returned
    // TaskTabViewModel, whose own Dispose() disposes it, and every call site uses `using var`.
#pragma warning disable CA2000
    private TaskTabViewModel Create(IWorkflowOrchestrator orchestrator)
    {
        var terminal = new TerminalViewModel(
            new WebViewEnvironmentProvider(),
            new ConPtySessionFactory(),
            Dispatcher.CurrentDispatcher);

        return new TaskTabViewModel(
            new TaskFolderService(),
            orchestrator,
            _settings,
            new FakeTaskStateStore(),
            new StubDirectoryPicker(),
            terminal,
            folderDebounce: TimeSpan.Zero,
            startupErrors: []);
    }
#pragma warning restore CA2000

    private sealed class CapturingOrchestrator : IWorkflowOrchestrator
    {
        public WorkflowRunRequest? Request { get; private set; }

        public Task RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    private sealed class StubDirectoryPicker : IDirectoryPickerService
    {
        public string? PickDirectory(string? initialDirectory, string title = IDirectoryPickerService.DefaultTitle) => null;
    }

    // -----------------------------------------------------------------------------------------
    // Fixture
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Builds one payload over the six fixture entries: the first <paramref name="complete"/> are
    /// complete, the next <paramref name="failed"/> failed with a reason nothing like their title,
    /// and the rest pending - so <c>Total == Completed + Failed + pending</c> pins the dimension the
    /// counts ignore.
    /// </summary>
    private static SubtaskProgress Payload(SubtaskStage stage, int complete, int failed, string? currentTitle)
    {
        var states = new List<SubtaskState>();

        for (var i = 0; i < SixEntries.Length; i++)
        {
            if (i < complete)
            {
                states.Add(new SubtaskState(SixEntries[i], SubtaskStatus.Complete));
            }
            else if (i < complete + failed)
            {
                states.Add(new SubtaskState(SixEntries[i], SubtaskStatus.Failed, $"Grund {i}"));
            }
            else
            {
                states.Add(new SubtaskState(SixEntries[i], SubtaskStatus.Pending));
            }
        }

        return new SubtaskProgress(stage, complete, states.Count, failed, currentTitle, states);
    }

    private TaskPaths TaskPathsOnDisk()
    {
        var paths = new TaskPaths(_workspace, TaskName);
        Directory.CreateDirectory(paths.TaskDirectory);
        return paths;
    }

    private static TaskState Journal(TaskPaths paths)
    {
        File.WriteAllText(paths.SpecAbsolute, "Spezifikation");
        File.WriteAllText(paths.PlanAbsolute, "Plan");
        File.WriteAllText(paths.ReviewAbsolute, "Review");

        var state = new TaskState { TaskDescription = "die Beschreibung" };
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.Specification, PhaseStatus.Completed, DateTimeOffset.UtcNow));
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.Review, PhaseStatus.Completed, DateTimeOffset.UtcNow));
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.ResolveReview, PhaseStatus.Completed, DateTimeOffset.UtcNow));
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.Implementation, PhaseStatus.Pending, null));
        state.SubtasksEnabled = true;
        state.WorkflowDirectory = null;

        return state;
    }

    /// <summary>Writes the ordered index and the per-entry evidence for three complete, two failed.</summary>
    private void WriteLedger()
    {
        var tracking = new SubtaskPaths(_tracking, TaskName);
        Directory.CreateDirectory(tracking.TaskDirectory);

        File.WriteAllText(
            tracking.ResultAbsolute,
            $$"""{ "version": 1, "subtasks": {{System.Text.Json.JsonSerializer.Serialize(SixEntries)}} }""");

        for (var i = 0; i < SixEntries.Length; i++)
        {
            var title = SixEntries[i];
            Directory.CreateDirectory(tracking.SubtaskDirectory(title));
            File.WriteAllText(tracking.SubtaskMarkdown(title), $"Beschreibung von {title}");

            var payload = i switch
            {
                < 3 => """{ "status": "complete" }""",
                < 5 => $$"""{ "status": "failed", "failreason": "Grund {{i}}" }""",
                _ => """{ "status": "pending" }""",
            };

            File.WriteAllText(tracking.SubtaskStatusFile(title), payload);
        }
    }

    private TaskTabViewModel StartAFreshSubtaskRun(CapturingOrchestrator orchestrator)
    {
        var vm = Create(orchestrator);
        vm.WorkingDirectory = _workspace;
        vm.TaskName = TaskName;
        vm.SubtasksEnabled = true;
        vm.WorkflowDirectory = _tracking;

        vm.StartWorkflowCommand.Execute(null);
        return vm;
    }

    private static async Task Settle(Func<bool> condition, string because)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), because);
    }

    // -----------------------------------------------------------------------------------------
    // Requirement 3.2: the run gets a sink, and what it publishes reaches the indicator
    // -----------------------------------------------------------------------------------------

    [StaFact]
    public void StartWorkflow_HandsASubtaskProgressSinkToTheRun()
    {
        var orchestrator = new CapturingOrchestrator();
        using var vm = StartAFreshSubtaskRun(orchestrator);

        Assert.NotNull(orchestrator.Request);
        Assert.NotNull(orchestrator.Request.SubtaskProgress);
    }

    [WpfFact]
    public async Task AReportPublishedByTheRun_ReachesTheIndicatorOnTheUiThread()
    {
        var orchestrator = new CapturingOrchestrator();
        using var vm = StartAFreshSubtaskRun(orchestrator);

        var sink = orchestrator.Request?.SubtaskProgress;
        Assert.NotNull(sink);

        var dispatcher = Dispatcher.CurrentDispatcher;
        var offThread = 0;
        var notifications = 0;

        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            Interlocked.Increment(ref notifications);

            if (!dispatcher.CheckAccess())
            {
                Interlocked.Increment(ref offThread);
            }
        }

        vm.SubtaskIndicator.PropertyChanged += OnChanged;

        try
        {
            // Reported from a pool thread, which is where WorkflowOrchestrator reports from.
            await Task.Run(() => sink.Report(Payload(SubtaskStage.Running, complete: 3, failed: 2, "ST-004-testen")));

            await Settle(
                () => vm.SubtaskIndicator.ProgressText == "3 von 6",
                "the published payload never reached the indicator");
        }
        finally
        {
            vm.SubtaskIndicator.PropertyChanged -= OnChanged;
        }

        Assert.Equal("2 fehlgeschlagen", vm.SubtaskIndicator.FailedText);
        Assert.True(vm.SubtaskIndicator.HasFailures);

        // Requirement 3.7: the reason is readable while the run is still going, not only afterwards.
        Assert.NotNull(vm.SubtaskIndicator.FailureTooltip);
        Assert.Contains("ST-004-testen: Grund 3", vm.SubtaskIndicator.FailureTooltip, StringComparison.Ordinal);
        Assert.Contains("ST-005-melden: Grund 4", vm.SubtaskIndicator.FailureTooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("ST-006-abschliessen", vm.SubtaskIndicator.FailureTooltip, StringComparison.Ordinal);
        Assert.True(vm.IsRunning, "the run must still be going while the failure is shown");

        // The sink has to be the UI-thread Progress<T>: built anywhere else it has no
        // synchronization context, invokes the callback on the reporting thread and lets
        // requirement 3.7's ordered states latch out of order.
        Assert.True(notifications > 0, "the indicator raised no change notification at all");
        Assert.Equal(0, Volatile.Read(ref offThread));
    }

    [WpfFact]
    public async Task SuccessiveReports_AdvanceTheDisplayedCountWithoutUserInteraction()
    {
        var orchestrator = new CapturingOrchestrator();
        using var vm = StartAFreshSubtaskRun(orchestrator);

        var sink = orchestrator.Request?.SubtaskProgress;
        Assert.NotNull(sink);

        await Task.Run(() => sink.Report(new SubtaskProgress(SubtaskStage.Decomposing, 0, 0, 0, null, [])));
        await Settle(
            () => vm.SubtaskIndicator.ProgressText == "Zerlegung läuft…",
            "decomposition entry never reached the indicator");

        await Task.Run(() => sink.Report(Payload(SubtaskStage.Running, complete: 1, failed: 3, "ST-005-melden")));
        await Settle(
            () => vm.SubtaskIndicator.ProgressText == "1 von 6",
            "the first subtask's payload never reached the indicator");

        Assert.Equal("3 fehlgeschlagen", vm.SubtaskIndicator.FailedText);

        await Task.Run(() => sink.Report(Payload(SubtaskStage.Running, complete: 2, failed: 3, "ST-006-abschliessen")));
        await Settle(
            () => vm.SubtaskIndicator.ProgressText == "2 von 6",
            "the count did not advance after the next subtask");

        Assert.True(vm.IsRunning, "the count must advance while the run continues");
    }

    // -----------------------------------------------------------------------------------------
    // Requirement 3.4: grey with no counts before phase 4 - and requirement 4.5's counts kept
    // for a run that starts AT phase 4
    // -----------------------------------------------------------------------------------------

    [StaFact]
    public void StartingARunBeforePhaseFour_ResetsTheIndicator()
    {
        var orchestrator = new CapturingOrchestrator();
        using var vm = Create(orchestrator);
        vm.WorkingDirectory = _workspace;
        vm.TaskName = TaskName;
        vm.SubtasksEnabled = true;
        vm.WorkflowDirectory = _tracking;

        // Whatever the row was showing, phases 1-3 are about to run and requirement 3.4 wants grey.
        vm.SubtaskIndicator.Apply(Payload(SubtaskStage.Running, complete: 3, failed: 2, "ST-004-testen"));

        vm.StartWorkflowCommand.Execute(null);

        Assert.NotNull(orchestrator.Request);
        Assert.Equal(WorkflowPhase.Specification, orchestrator.Request.StartPhase);

        Assert.Equal("0 von 0", vm.SubtaskIndicator.ProgressText);
        Assert.Equal(PhaseStatus.Pending, vm.SubtaskIndicator.Status);
        Assert.Equal(string.Empty, vm.SubtaskIndicator.FailedText);
        Assert.False(vm.SubtaskIndicator.HasFailures);
        Assert.Null(vm.SubtaskIndicator.FailureTooltip);
    }

    [StaFact]
    public void ContinuingARecoveredSubtaskTask_KeepsTheCountsDerivedFromDisk()
    {
        // The ruling task 6.4 had to make. A recovered task's indicator is not a previous run's
        // leftovers: task 5.2 read it off the tracking files moments ago (requirement 4.5), and the
        // run is about to resume AT phase 4, where requirement 3.4's grey-before-phase-4 rule has
        // nothing left to say. Wiping it would make the tab claim '0 von 6' work done - and would
        // keep claiming it if the run failed before publishing anything, which the phase-4
        // configuration check can do (requirement 1.8).
        var orchestrator = new CapturingOrchestrator();
        using var vm = Create(orchestrator);

        var paths = TaskPathsOnDisk();
        var state = Journal(paths);
        state.WorkflowDirectory = _tracking;
        WriteLedger();

        vm.LoadForResume(new RecoverableTask(paths, state, WorkflowPhase.Implementation));
        Assert.Equal("3 von 6", vm.SubtaskIndicator.ProgressText);

        vm.StartWorkflowCommand.Execute(null);

        Assert.NotNull(orchestrator.Request);
        Assert.Equal(WorkflowPhase.Implementation, orchestrator.Request.StartPhase);

        Assert.Equal("3 von 6", vm.SubtaskIndicator.ProgressText);
        Assert.Equal("2 fehlgeschlagen", vm.SubtaskIndicator.FailedText);
        Assert.True(vm.SubtaskIndicator.HasFailures);
        Assert.NotNull(vm.SubtaskIndicator.FailureTooltip);
        Assert.Contains("ST-004-testen: Grund 3", vm.SubtaskIndicator.FailureTooltip, StringComparison.Ordinal);

        // Still grey: nothing is running yet from the indicator's point of view, and the stage the
        // ledger seeded is Idle (task 5.2's committed choice).
        Assert.Equal(PhaseStatus.Pending, vm.SubtaskIndicator.Status);
    }

    [StaFact]
    public void ContinuingARecoveredTaskThatStillHasEarlierPhasesToRun_ResetsTheIndicator()
    {
        // The other side of the same ruling: the seed is kept only when phase 4 is what starts. A
        // task resuming at phase 2 runs phases 2 and 3 first, and requirement 3.4 is grey there.
        var orchestrator = new CapturingOrchestrator();
        using var vm = Create(orchestrator);

        var paths = TaskPathsOnDisk();
        var state = Journal(paths);
        state.WorkflowDirectory = _tracking;
        WriteLedger();

        vm.LoadForResume(new RecoverableTask(paths, state, WorkflowPhase.Review));
        Assert.Equal("3 von 6", vm.SubtaskIndicator.ProgressText);

        vm.StartWorkflowCommand.Execute(null);

        Assert.NotNull(orchestrator.Request);
        Assert.Equal(WorkflowPhase.Review, orchestrator.Request.StartPhase);

        Assert.Equal("0 von 0", vm.SubtaskIndicator.ProgressText);
        Assert.Equal(PhaseStatus.Pending, vm.SubtaskIndicator.Status);
        Assert.False(vm.SubtaskIndicator.HasFailures);
        Assert.Null(vm.SubtaskIndicator.FailureTooltip);
    }

    [StaFact]
    public void ARefusedStart_LeavesTheIndicatorAlone()
    {
        // The reset belongs to a run that actually begins. A start the gate turns away must not
        // destroy the counts the user is looking at while they fix the tracking directory.
        var orchestrator = new CapturingOrchestrator();
        using var vm = Create(orchestrator);

        var paths = TaskPathsOnDisk();
        var state = Journal(paths);
        state.WorkflowDirectory = _tracking;
        WriteLedger();

        vm.LoadForResume(new RecoverableTask(paths, state, WorkflowPhase.Review));

        Directory.Delete(Path.Combine(_tracking, SubtaskPaths.TemplateFolderName), recursive: true);

        vm.StartWorkflowCommand.Execute(null);

        Assert.Null(orchestrator.Request);
        Assert.False(vm.IsRunning);
        Assert.Equal("3 von 6", vm.SubtaskIndicator.ProgressText);
    }

    // -----------------------------------------------------------------------------------------
    // Requirement 3.1: the indicator belongs to subtask mode, so switching the mode off clears it
    // -----------------------------------------------------------------------------------------

    [StaFact]
    public void SwitchingSubtaskModeOff_ResetsTheIndicator()
    {
        var orchestrator = new CapturingOrchestrator();
        using var vm = Create(orchestrator);
        vm.WorkingDirectory = _workspace;
        vm.TaskName = TaskName;
        vm.SubtasksEnabled = true;
        vm.WorkflowDirectory = _tracking;

        vm.SubtaskIndicator.Apply(Payload(SubtaskStage.Running, complete: 3, failed: 2, "ST-004-testen"));

        vm.SubtasksEnabled = false;

        Assert.Equal("0 von 0", vm.SubtaskIndicator.ProgressText);
        Assert.Equal(PhaseStatus.Pending, vm.SubtaskIndicator.Status);
        Assert.Equal(string.Empty, vm.SubtaskIndicator.FailedText);
        Assert.False(vm.SubtaskIndicator.HasFailures);
        Assert.Null(vm.SubtaskIndicator.FailureTooltip);
    }

    [StaFact]
    public void SwitchingSubtaskModeOn_DoesNotClearWhatTheLedgerSeeded()
    {
        // Requirement 4.5's restored counts survive a mode toggle that ends where it started: only
        // switching OFF clears, and LoadForResume's own restore assignment must not wipe its seed.
        var orchestrator = new CapturingOrchestrator();
        using var vm = Create(orchestrator);

        var paths = TaskPathsOnDisk();
        var state = Journal(paths);
        state.WorkflowDirectory = _tracking;
        WriteLedger();

        vm.LoadForResume(new RecoverableTask(paths, state, WorkflowPhase.Implementation));

        Assert.True(vm.SubtasksEnabled);
        Assert.Equal("3 von 6", vm.SubtaskIndicator.ProgressText);
        Assert.Equal("2 fehlgeschlagen", vm.SubtaskIndicator.FailedText);
    }
}
