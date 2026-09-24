using System.IO;
using System.Text;
using System.Windows.Threading;
using Workflow.Models;
using Workflow.Services;
using Workflow.Terminal;
using Workflow.Tests.Fakes;
using Workflow.ViewModels;

namespace Workflow.Tests;

/// <remarks>
/// <para>
/// Task 5.2: the tab restores a recovered task's subtask mode and tracking directory, seeds the
/// subtask indicator from the tracking files on disk, and hands the restored configuration to the
/// run so a continued task takes the subtask branch (requirements 4.1, 4.2, 4.5).
/// </para>
/// <para>
/// Every ledger fixture here is deliberately asymmetric: six entries, three complete, two failed
/// and one pending, so the four numbers the indicator derives are pairwise distinct and a mutant
/// that swaps two of them fails rather than slips through. The pending entry pins the ignored
/// dimension structurally - <c>Total == Completed + Failed + pending</c>.
/// </para>
/// </remarks>
public sealed class TaskTabSubtaskRecoveryTests : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private const string TaskName = "alpha";

    private readonly string _root;
    private readonly string _workspace;
    private readonly string _tracking;
    private readonly SettingsService _settings;

    public TaskTabSubtaskRecoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wf-tab52-" + Guid.NewGuid().ToString("N"));
        _workspace = Path.Combine(_root, "arbeit");
        _tracking = Path.Combine(_root, "workflows");
        Directory.CreateDirectory(_workspace);

        // Task 6.3 gates the start action on the tracking directory being a real tracking
        // repository (requirement 1.3), so the fixture now carries the marker folder a checked-out
        // Workflows repository has. Fixture only: no assertion in this file changes.
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
    private TaskTabViewModel Create(
        IWorkflowOrchestrator? orchestrator = null,
        ITaskStateStore? stateStore = null)
    {
        var terminal = new TerminalViewModel(
            new WebViewEnvironmentProvider(),
            new ConPtySessionFactory(),
            Dispatcher.CurrentDispatcher);

        return new TaskTabViewModel(
            new TaskFolderService(),
            orchestrator ?? new StubOrchestrator(),
            _settings,
            stateStore ?? new FakeTaskStateStore(),
            new StubDirectoryPicker(),
            terminal,
            folderDebounce: TimeSpan.Zero,
            startupErrors: []);
    }
#pragma warning restore CA2000

    private sealed class StubOrchestrator : IWorkflowOrchestrator
    {
        public Task RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.Infinite, cancellationToken);
    }

    private sealed class CapturingOrchestrator : IWorkflowOrchestrator
    {
        public WorkflowRunRequest? Request { get; private set; }

        public Task RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    private sealed class ThrowingOrchestrator(Exception failure) : IWorkflowOrchestrator
    {
        public Task RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken) =>
            Task.FromException(failure);
    }

    private sealed class StubDirectoryPicker : IDirectoryPickerService
    {
        public string? PickDirectory(string? initialDirectory, string title = IDirectoryPickerService.DefaultTitle) => null;
    }

    // -----------------------------------------------------------------------------------------
    // Fixture
    // -----------------------------------------------------------------------------------------

    private TaskPaths TaskPathsOnDisk()
    {
        var paths = new TaskPaths(_workspace, TaskName);
        Directory.CreateDirectory(paths.TaskDirectory);
        return paths;
    }

    /// <summary>A journal whose first three phases are Completed and backed by artefacts on disk.</summary>
    /// <param name="paths">The task whose artefacts are written.</param>
    /// <param name="implementation">The recorded status of the implementation phase.</param>
    private static TaskState Journal(TaskPaths paths, PhaseStatus implementation)
    {
        File.WriteAllText(paths.SpecAbsolute, "Spezifikation", Utf8);
        File.WriteAllText(paths.PlanAbsolute, "Plan", Utf8);
        File.WriteAllText(paths.ReviewAbsolute, "Review", Utf8);

        var state = new TaskState
        {
            TaskDescription = "die Beschreibung",
            CreatedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };

        state.Phases.Add(new TaskPhaseState(WorkflowPhase.Specification, PhaseStatus.Completed, DateTimeOffset.UtcNow));
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.Review, PhaseStatus.Completed, DateTimeOffset.UtcNow));
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.ResolveReview, PhaseStatus.Completed, DateTimeOffset.UtcNow));
        state.Phases.Add(new TaskPhaseState(
            WorkflowPhase.Implementation,
            implementation,
            implementation == PhaseStatus.Completed ? DateTimeOffset.UtcNow : null));

        return state;
    }

    /// <summary>
    /// Writes an ordered index and the per-entry evidence: the first <paramref name="complete"/>
    /// entries publish <c>complete</c>, the next <paramref name="failed"/> publish <c>failed</c>
    /// with a distinct reason, and the remainder stay pending with a usable description.
    /// </summary>
    private void WriteLedger(string[] titles, int complete, int failed)
    {
        var tracking = new SubtaskPaths(_tracking, TaskName);
        Directory.CreateDirectory(tracking.TaskDirectory);

        File.WriteAllText(
            tracking.ResultAbsolute,
            $$"""{ "version": 1, "subtasks": {{System.Text.Json.JsonSerializer.Serialize(titles)}} }""",
            Utf8);

        for (var i = 0; i < titles.Length; i++)
        {
            var title = titles[i];
            Directory.CreateDirectory(tracking.SubtaskDirectory(title));

            // A pending entry without a usable description would be Failed for a reason that has
            // nothing to do with the rule under test, so every entry keeps one.
            File.WriteAllText(tracking.SubtaskMarkdown(title), $"Beschreibung von {title}", Utf8);

            if (i < complete)
            {
                File.WriteAllText(tracking.SubtaskStatusFile(title), """{ "status": "complete" }""", Utf8);
            }
            else if (i < complete + failed)
            {
                File.WriteAllText(
                    tracking.SubtaskStatusFile(title),
                    $$"""{ "status": "failed", "failreason": "Grund {{i}}" }""",
                    Utf8);
            }
            else
            {
                File.WriteAllText(tracking.SubtaskStatusFile(title), """{ "status": "pending" }""", Utf8);
            }
        }
    }

    private static readonly string[] SixEntries =
    [
        "ST-001-lesen",
        "ST-002-pruefen",
        "ST-003-schreiben",
        "ST-004-testen",
        "ST-005-melden",
        "ST-006-abschliessen",
    ];

    // -----------------------------------------------------------------------------------------
    // Requirement 4.1: the mode and the tracking directory come back
    // -----------------------------------------------------------------------------------------

    [StaFact]
    public void LoadForResume_SubtaskTask_RestoresTheModeAndTheTrackingDirectory()
    {
        using var vm = Create();
        var paths = TaskPathsOnDisk();
        var state = Journal(paths, PhaseStatus.Pending);
        state.SubtasksEnabled = true;
        state.WorkflowDirectory = _tracking;

        vm.LoadForResume(new RecoverableTask(paths, state, WorkflowPhase.Implementation));

        Assert.True(vm.SubtasksEnabled);
        Assert.Equal(_tracking, vm.WorkflowDirectory);

        // The normal working-folder synchronisation is what maintains the working-directory MRU.
        // The tracking directory must not travel through it: a restore that assigned the tracking
        // path to WorkingDirectory - or that let the folder sync run - would show up here.
        Assert.Equal(_workspace, vm.WorkingDirectory);
        Assert.DoesNotContain(_tracking, _settings.Settings.RecentDirectories);
    }

    [StaFact]
    public void LoadForResume_NormalTask_LeavesSubtaskModeOffAndTheIndicatorEmpty()
    {
        // Requirement 1.7: a journal written before the subtask fields existed reads as disabled.
        using var vm = Create();
        var paths = TaskPathsOnDisk();

        vm.LoadForResume(new RecoverableTask(paths, Journal(paths, PhaseStatus.Pending), WorkflowPhase.Implementation));

        Assert.False(vm.SubtasksEnabled);
        Assert.Null(vm.WorkflowDirectory);
        Assert.Equal("0 von 0", vm.SubtaskIndicator.ProgressText);
        Assert.Equal(PhaseStatus.Pending, vm.SubtaskIndicator.Status);
    }

    // -----------------------------------------------------------------------------------------
    // Requirement 4.5: progress is derived from the tracking files, with no persisted cursor
    // -----------------------------------------------------------------------------------------

    [StaFact]
    public void LoadForResume_SubtaskTask_SeedsTheIndicatorFromTheLedger()
    {
        using var vm = Create();
        var paths = TaskPathsOnDisk();
        var state = Journal(paths, PhaseStatus.Pending);
        state.SubtasksEnabled = true;
        state.WorkflowDirectory = _tracking;
        WriteLedger(SixEntries, complete: 3, failed: 2);

        vm.LoadForResume(new RecoverableTask(paths, state, WorkflowPhase.Implementation));

        Assert.Equal("3 von 6", vm.SubtaskIndicator.ProgressText);
        Assert.Equal("2 fehlgeschlagen", vm.SubtaskIndicator.FailedText);
        Assert.True(vm.SubtaskIndicator.HasFailures);

        // Requirement 3.7: the restored payload carries the ordered states, so the failure reasons
        // reach the tooltip on recovery exactly as they do during a live run.
        Assert.NotNull(vm.SubtaskIndicator.FailureTooltip);
        Assert.Contains("ST-004-testen: Grund 3", vm.SubtaskIndicator.FailureTooltip, StringComparison.Ordinal);
        Assert.Contains("ST-005-melden: Grund 4", vm.SubtaskIndicator.FailureTooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("ST-006-abschliessen", vm.SubtaskIndicator.FailureTooltip, StringComparison.Ordinal);
    }

    [StaFact]
    public void LoadForResume_SubtaskTaskWithWorkOutstanding_SeedsTheIndicatorGrey()
    {
        // Nothing is running yet: the user has not pressed Continue. Requirement 3.4 reserves
        // yellow for "while running", and the design says a recovered tab's indicator may be grey
        // while its counts still reflect disk. Seeding SubtaskStage.Idle is that choice.
        using var vm = Create();
        var paths = TaskPathsOnDisk();
        var state = Journal(paths, PhaseStatus.Pending);
        state.SubtasksEnabled = true;
        state.WorkflowDirectory = _tracking;
        WriteLedger(SixEntries, complete: 3, failed: 2);

        vm.LoadForResume(new RecoverableTask(paths, state, WorkflowPhase.Implementation));

        Assert.Equal(PhaseStatus.Pending, vm.SubtaskIndicator.Status);
    }

    [StaFact]
    public void LoadForResume_SubtaskTaskWhoseEntriesAreAllComplete_SeedsTheIndicatorGreen()
    {
        // Green is decided by the counts alone (requirement 3.4, design issue 6a), so the Idle
        // stage above does not hold a finished task back.
        using var vm = Create();
        var paths = TaskPathsOnDisk();
        var state = Journal(paths, PhaseStatus.Completed);
        state.SubtasksEnabled = true;
        state.WorkflowDirectory = _tracking;
        WriteLedger(SixEntries, complete: 6, failed: 0);

        vm.LoadForResume(new RecoverableTask(paths, state, WorkflowPhase.Implementation));

        Assert.Equal(PhaseStatus.Completed, vm.SubtaskIndicator.Status);
        Assert.Equal("6 von 6", vm.SubtaskIndicator.ProgressText);
        Assert.False(vm.SubtaskIndicator.HasFailures);
    }

    [StaFact]
    public void LoadForResume_SubtaskTaskWithAnUnreadableIndex_ClaimsNoCounts()
    {
        // Requirement 4.6: unreadable evidence is not "nothing is done" and must never complete a
        // task. Zero counts can never be green, so the indicator says only what it knows.
        using var vm = Create();
        var paths = TaskPathsOnDisk();
        var state = Journal(paths, PhaseStatus.Completed);
        state.SubtasksEnabled = true;
        state.WorkflowDirectory = _tracking;

        var tracking = new SubtaskPaths(_tracking, TaskName);
        Directory.CreateDirectory(tracking.TaskDirectory);
        File.WriteAllText(tracking.ResultAbsolute, """{ "subtasks": ["ST-001-lesen" """, Utf8);

        vm.LoadForResume(new RecoverableTask(paths, state, WorkflowPhase.Implementation));

        Assert.Equal("0 von 0", vm.SubtaskIndicator.ProgressText);
        Assert.Equal(PhaseStatus.Pending, vm.SubtaskIndicator.Status);
        Assert.False(vm.SubtaskIndicator.HasFailures);

        // The condition is surfaced by the classification the reconciliation already exposes; the
        // tab must not invent a second vocabulary for it.
        Assert.Equal(SubtaskEvidence.Unavailable, PhaseReconciliation.Evaluate(paths, state));
    }

    [StaTheory]
    [InlineData("")]
    [InlineData("   ")]
    public void LoadForResume_SubtaskTaskWithABlankTrackingPath_RestoresItVerbatimAndClaimsNoCounts(string directory)
    {
        // The journal stores the directory verbatim so a blank choice stays distinguishable from
        // "never chosen". Composing a path set from it throws, and that is not a crash.
        using var vm = Create();
        var paths = TaskPathsOnDisk();
        var state = Journal(paths, PhaseStatus.Pending);
        state.SubtasksEnabled = true;
        state.WorkflowDirectory = directory;

        vm.LoadForResume(new RecoverableTask(paths, state, WorkflowPhase.Implementation));

        Assert.True(vm.SubtasksEnabled);
        Assert.Equal(directory, vm.WorkflowDirectory);
        Assert.Equal("0 von 0", vm.SubtaskIndicator.ProgressText);
        Assert.Equal(PhaseStatus.Pending, vm.SubtaskIndicator.Status);
    }

    // -----------------------------------------------------------------------------------------
    // Requirement 4.2: continuing the task actually takes the subtask branch
    // -----------------------------------------------------------------------------------------

    [StaFact]
    public void StartWorkflow_OnARecoveredSubtaskTask_HandsTheRestoredConfigurationToTheRun()
    {
        var orchestrator = new CapturingOrchestrator();
        using var vm = Create(orchestrator: orchestrator);
        var paths = TaskPathsOnDisk();
        var state = Journal(paths, PhaseStatus.Pending);
        state.SubtasksEnabled = true;
        state.WorkflowDirectory = _tracking;
        WriteLedger(SixEntries, complete: 3, failed: 2);

        vm.LoadForResume(new RecoverableTask(paths, state, WorkflowPhase.Implementation));
        vm.StartWorkflowCommand.Execute(null);

        Assert.NotNull(orchestrator.Request);
        Assert.Equal(WorkflowPhase.Implementation, orchestrator.Request.StartPhase);

        // Without this the run takes the normal implementation branch and the skip-completed loop
        // is never reached, so "continuing runs only the entries that are not complete" cannot hold.
        // Task 2.5 made the member a deferred capture, so the assertion forces it exactly as
        // phase-4 entry would. The subject and the claim are unchanged.
        Assert.True(SubtaskConfiguration.IsEnabled(orchestrator.Request.Subtasks?.Value));
        Assert.Equal(_tracking, orchestrator.Request.Subtasks!.Value.WorkflowDirectory);
    }

    [StaFact]
    public void StartWorkflow_HandsOverAnUnforcedCapture_SoPhasesOneToThreeStayEditable()
    {
        // Requirement 1.6: the snapshot belongs to phase-4 ENTRY, not to run entry. Task 5.2 wired
        // the capture into StartWorkflow, which coincides with phase-4 entry only for a resumed
        // task; task 2.5 defers it, so the request must leave the run holding an unforced capture.
        var orchestrator = new CapturingOrchestrator();
        using var vm = Create(orchestrator: orchestrator);
        vm.WorkingDirectory = _workspace;
        vm.TaskName = TaskName;

        vm.StartWorkflowCommand.Execute(null);

        Assert.NotNull(orchestrator.Request);
        Assert.NotNull(orchestrator.Request.Subtasks);
        Assert.False(orchestrator.Request.Subtasks.IsValueCreated);
    }

    [StaFact]
    public void StartWorkflow_OnANormalTask_HandsADisabledConfigurationToTheRun()
    {
        // Requirement 2.1: a task that never selected subtask mode keeps the existing behaviour.
        var orchestrator = new CapturingOrchestrator();
        using var vm = Create(orchestrator: orchestrator);
        vm.WorkingDirectory = _workspace;
        vm.TaskName = TaskName;

        vm.StartWorkflowCommand.Execute(null);

        Assert.NotNull(orchestrator.Request);
        Assert.False(SubtaskConfiguration.IsEnabled(orchestrator.Request.Subtasks?.Value));
    }

    [StaFact]
    public async Task AFailingSubtaskConfiguration_SurfacesItsGermanMessageInsteadOfAnUnobservedException()
    {
        // Requirements 1.8 and 2.9. Wiring the configuration into the request makes this exception
        // reachable for the first time; RunAsync's catch list is exhaustive by design and did not
        // name it, so it would otherwise become an unobserved task exception while the tab silently
        // flipped IsRunning back to false (spec section 12.1).
        var unobserved = 0;
        void OnUnobserved(object? s, UnobservedTaskExceptionEventArgs e) => Interlocked.Increment(ref unobserved);
        TaskScheduler.UnobservedTaskException += OnUnobserved;

        try
        {
            const string Message = "Das Workflow-Verzeichnis enthält keinen Ordner \"task_template\".";
            using var vm = Create(orchestrator: new ThrowingOrchestrator(new SubtaskConfigurationException(Message)));
            vm.WorkingDirectory = _workspace;
            vm.TaskName = TaskName;

            vm.StartWorkflowCommand.Execute(null);

            for (var i = 0; i < 50 && vm.IsRunning; i++)
            {
                await Task.Delay(20);
            }

            Assert.False(vm.IsRunning);
            Assert.Equal(Message, vm.ValidationMessage);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            Assert.Equal(0, Volatile.Read(ref unobserved));
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    // -----------------------------------------------------------------------------------------
    // Carried directive item (d): the re-arm call site, which task 5.1 taught to consult the
    // ledger through the shared PhaseReconciliation helper without touching this file.
    // -----------------------------------------------------------------------------------------

    [StaFact]
    public void TypingTheNameOfACompletedSubtaskTask_DoesNotOfferContinue()
    {
        // A subtask task has no done-marker by design. Before 5.1 the re-arm demoted it on every
        // restart and offered 'Continue workflow' at implementation (requirement 4.3).
        var store = new FakeTaskStateStore();
        var paths = TaskPathsOnDisk();
        var state = Journal(paths, PhaseStatus.Completed);
        state.SubtasksEnabled = true;
        state.WorkflowDirectory = _tracking;
        store.Seed(paths, state);
        WriteLedger(SixEntries, complete: 6, failed: 0);

        using var vm = Create(stateStore: store);
        vm.WorkingDirectory = _workspace;
        vm.TaskName = TaskName;

        Assert.Null(vm.ResumePhase);
        Assert.Equal("Start workflow", vm.StartButtonLabel);
        Assert.False(File.Exists(paths.DoneAbsolute));
        Assert.Empty(store.Replacements);
    }

    [StaFact]
    public void TypingTheNameOfAnUnfinishedSubtaskTask_OffersContinueAtImplementation()
    {
        // The other direction of the same rule: positively unfinished evidence still demotes, so
        // the re-arm is a ledger consultation and not a blanket "never demote a subtask task".
        var store = new FakeTaskStateStore();
        var paths = TaskPathsOnDisk();
        var state = Journal(paths, PhaseStatus.Completed);
        state.SubtasksEnabled = true;
        state.WorkflowDirectory = _tracking;
        store.Seed(paths, state);
        WriteLedger(SixEntries, complete: 3, failed: 2);

        using var vm = Create(stateStore: store);
        vm.WorkingDirectory = _workspace;
        vm.TaskName = TaskName;

        Assert.Equal(WorkflowPhase.Implementation, vm.ResumePhase);
        Assert.Equal("Continue workflow", vm.StartButtonLabel);
        Assert.NotEmpty(store.Replacements);
    }
}
