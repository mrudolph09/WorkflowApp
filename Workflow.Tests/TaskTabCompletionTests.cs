using System.IO;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Workflow.Models;
using Workflow.Services;
using Workflow.Terminal;
using Workflow.Tests.Fakes;
using Workflow.ViewModels;

namespace Workflow.Tests;

/// <remarks>
/// <para>
/// Task 6.6: <c>Task abschliessen</c> signals the run, awaits its end, records the completion as an
/// explicit manual override - Implementation <c>Completed</c> plus
/// <c>ImplementationCompletedManually</c>, both halves of design decision 6b - and only then
/// requests the tab close (requirement 5.3, design "Presentation and Recovery"). Both halves of
/// requirement 5.4 are pinned here too: recovery must find nothing left to resume, and the manual
/// override must not turn the subtask indicator green - it keeps showing the real counts.
/// </para>
/// <para>
/// Every ordering assertion below is taken <em>at the moment the later event happens</em>, never
/// after the command has returned. An assertion made afterwards would be vacuous: by then the run
/// has ended whatever the order was. <see cref="WindingDownOrchestrator"/> exists for the same
/// reason - its run does not end when the signal arrives, it keeps going for
/// <see cref="WindingDownOrchestrator.WindDown"/> afterwards, exactly as the real subtask loop does
/// when it settles the running entry and re-reads the ledger before returning. An implementation
/// that closed first and awaited second therefore observes a run that has not ended.
/// </para>
/// </remarks>
public sealed class TaskTabCompletionTests : IDisposable
{
    private const string TaskName = "alpha";

    private readonly string _root;
    private readonly string _workspace;
    private readonly string _tracking;
    private readonly SettingsService _settings;

    public TaskTabCompletionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wf-tab66-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>A run that ends only on the manual signal, and not until a while after it.</summary>
    private sealed class WindingDownOrchestrator : IWorkflowOrchestrator
    {
        /// <summary>
        /// How long the run keeps going after the signal. Long enough that a close raised without
        /// awaiting the run lands inside this window on any machine this suite runs on.
        /// </summary>
        internal static readonly TimeSpan WindDown = TimeSpan.FromMilliseconds(250);

        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _signalled;

        public WorkflowRunRequest? Request { get; private set; }

        public bool SignalObserved => Volatile.Read(ref _signalled) != 0;

        public bool HasEnded => _ended.Task.IsCompleted;

        public Task RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            Request = request;

            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await request.ManualSignal.WaitAsync(cancellationToken);
                        Volatile.Write(ref _signalled, 1);

                        // The signal ends the waiting session; the run itself still has work to do.
                        await Task.Delay(WindDown, CancellationToken.None);
                        _ended.TrySetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        _ended.TrySetCanceled(CancellationToken.None);
                    }
                },
                CancellationToken.None);

            return _ended.Task;
        }
    }

    /// <summary>
    /// A journal that reports the exact moment the manual override is written, so the write can be
    /// ordered against the run's end and against the close.
    /// </summary>
    private sealed class ObservingStateStore(Action<bool> onManualCompletion) : ITaskStateStore
    {
        private readonly FakeTaskStateStore _inner = new();

        public IReadOnlyList<(string Directory, bool CompletedManually)> ManualCompletions =>
            [.. _inner.ManualCompletions];

        public TaskState? TryLoad(TaskPaths paths) => _inner.TryLoad(paths);

        public void SaveDescription(TaskPaths paths, string taskDescription) =>
            _inner.SaveDescription(paths, taskDescription);

        public void RecordPhase(TaskPaths paths, WorkflowPhase phase, PhaseStatus status) =>
            _inner.RecordPhase(paths, phase, status);

        public void ReplacePhases(TaskPaths paths, IReadOnlyList<TaskPhaseState> phases) =>
            _inner.ReplacePhases(paths, phases);

        public void SetDismissed(TaskPaths paths, bool dismissed) => _inner.SetDismissed(paths, dismissed);

        public void SaveSubtaskSettings(TaskPaths paths, bool enabled, string? workflowDirectory) =>
            _inner.SaveSubtaskSettings(paths, enabled, workflowDirectory);

        public void SetImplementationCompletedManually(TaskPaths paths, bool completedManually)
        {
            _inner.SetImplementationCompletedManually(paths, completedManually);
            onManualCompletion(completedManually);
        }
    }

    private sealed class StubDirectoryPicker : IDirectoryPickerService
    {
        public string? PickDirectory(string? initialDirectory, string title = IDirectoryPickerService.DefaultTitle) => null;
    }

    // CA2000 cannot trace the disposal: TerminalViewModel's ownership passes into the returned
    // TaskTabViewModel, whose own Dispose() disposes it, and every call site uses `using var`.
#pragma warning disable CA2000
    private TaskTabViewModel Create(IWorkflowOrchestrator orchestrator, ITaskStateStore stateStore)
    {
        var terminal = new TerminalViewModel(
            new WebViewEnvironmentProvider(),
            new ConPtySessionFactory(),
            Dispatcher.CurrentDispatcher);

        return new TaskTabViewModel(
            new TaskFolderService(),
            orchestrator,
            _settings,
            stateStore,
            new StubDirectoryPicker(),
            terminal,
            folderDebounce: TimeSpan.Zero,
            startupErrors: []);
    }
#pragma warning restore CA2000

    /// <summary>Starts a subtask-mode run and puts it into the implementation phase.</summary>
    private TaskTabViewModel StartAnImplementationRun(
        IWorkflowOrchestrator orchestrator,
        ITaskStateStore stateStore)
    {
        var vm = Create(orchestrator, stateStore);
        vm.WorkingDirectory = _workspace;
        vm.TaskName = TaskName;
        vm.SubtasksEnabled = true;
        vm.WorkflowDirectory = _tracking;

        vm.StartWorkflowCommand.Execute(null);
        vm.ApplyProgress(new PhaseProgress(WorkflowPhase.Implementation, PhaseStatus.Active));

        return vm;
    }

    private static async Task Settle(Func<bool> condition, string because)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), because);
    }

    // -----------------------------------------------------------------------------------------
    // Requirement 5.3: signal, await the run's end, record the override, then close
    // -----------------------------------------------------------------------------------------

    [WpfFact]
    public async Task CompleteTask_SignalsTheRunAndClosesOnlyAfterItHasEnded()
    {
        var orchestrator = new WindingDownOrchestrator();
        using var vm = StartAnImplementationRun(orchestrator, new FakeTaskStateStore());

        bool? runHadEnded = null;
        var closes = 0;
        vm.CloseRequested += (_, _) =>
        {
            closes++;
            runHadEnded ??= orchestrator.HasEnded;
        };

        vm.CompleteTaskCommand.Execute(null);

        await Settle(() => closes > 0, "the task completion never requested the tab close");

        Assert.True(orchestrator.SignalObserved, "the run was never signalled");
        Assert.True(runHadEnded, "the close was requested while the run was still going");
        Assert.Equal(1, closes);
        Assert.False(vm.IsRunning);
    }

    [WpfFact]
    public async Task CompleteTask_RecordsTheManualOverrideAfterTheRunEndedAndBeforeTheClose()
    {
        var orchestrator = new WindingDownOrchestrator();

        bool? runHadEndedAtTheWrite = null;
        var store = new ObservingStateStore(_ => runHadEndedAtTheWrite ??= orchestrator.HasEnded);

        using var vm = StartAnImplementationRun(orchestrator, store);

        var overridesAtClose = -1;
        var closes = 0;
        vm.CloseRequested += (_, _) =>
        {
            closes++;
            if (overridesAtClose < 0)
            {
                overridesAtClose = store.ManualCompletions.Count;
            }
        };

        vm.CompleteTaskCommand.Execute(null);

        await Settle(() => closes > 0, "the task completion never requested the tab close");

        Assert.True(runHadEndedAtTheWrite, "the override was journalled while the run was still going");
        Assert.Equal(1, overridesAtClose);
    }

    [WpfFact]
    public async Task CompleteTask_MarksTheImplementationCompletedManuallyForThisTask()
    {
        var orchestrator = new WindingDownOrchestrator();
        var store = new FakeTaskStateStore();
        using var vm = StartAnImplementationRun(orchestrator, store);

        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        vm.CompleteTaskCommand.Execute(null);

        await Settle(() => closed, "the task completion never requested the tab close");

        var entry = Assert.Single(store.ManualCompletions);
        Assert.Equal(new TaskPaths(_workspace, TaskName).TaskDirectory, entry.Directory);
        Assert.True(entry.CompletedManually);

        // The marker is the half requirement 5.4 lets recovery read INSTEAD of the ledger. It is
        // only half: CompleteTask_LeavesNothingForRecoveryToResume covers the recorded phase status
        // that reconciliation needs before it ever looks at this flag.
        var state = store.TryLoad(new TaskPaths(_workspace, TaskName));
        Assert.NotNull(state);
        Assert.True(state.ImplementationCompletedManually);
    }

    /// <summary>
    /// Requirement 5.4, presentation half: a manual override is not an all-complete run, so the
    /// indicator keeps the counts the ledger actually reported instead of going green.
    /// </summary>
    [WpfFact]
    public async Task CompleteTask_LeavesTheIndicatorShowingTheRealSubtaskCounts()
    {
        var orchestrator = new WindingDownOrchestrator();
        using var vm = StartAnImplementationRun(orchestrator, new FakeTaskStateStore());

        var sink = orchestrator.Request?.SubtaskProgress;
        Assert.NotNull(sink);

        sink.Report(new SubtaskProgress(
            SubtaskStage.Running,
            Completed: 2,
            Total: 5,
            Failed: 1,
            "ST-003-schreiben",
            [new SubtaskState("ST-003-schreiben", SubtaskStatus.Failed, "Grund 3")]));

        await Settle(
            () => vm.SubtaskIndicator.ProgressText == "2 von 5",
            "the published payload never reached the indicator");

        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        vm.CompleteTaskCommand.Execute(null);

        await Settle(() => closed, "the task completion never requested the tab close");

        Assert.Equal("2 von 5", vm.SubtaskIndicator.ProgressText);
        Assert.Equal("1 fehlgeschlagen", vm.SubtaskIndicator.FailedText);
        Assert.NotEqual(PhaseStatus.Completed, vm.SubtaskIndicator.Status);
    }

    // -----------------------------------------------------------------------------------------
    // Requirement 5.3: the action stays enabled only while implementation runs
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// The re-entrancy posture, stated explicitly. The generated command is asynchronous and
    /// non-concurrent, so while the first invocation is still awaiting the run its
    /// <see cref="ICommand.CanExecute"/> is false and
    /// <see cref="System.Windows.Controls.Primitives.ButtonBase"/> - the only production caller -
    /// keeps the button disabled. Note what is NOT claimed: <c>AllowConcurrentExecutions = false</c>
    /// guards <c>CanExecute</c>, not <c>Execute</c>, so a caller that bypasses the framework and
    /// invokes <c>Execute</c> directly would start a second invocation. Nothing in the application
    /// does, and a second <see cref="TaskTabViewModel.CloseRequested"/> would be absorbed by
    /// <c>MainWindowViewModel.CloseTab</c>, which returns early for a tab it no longer holds.
    /// </summary>
    [WpfFact]
    public async Task CompleteTask_IsAnAsynchronousNonConcurrentCommand()
    {
        var orchestrator = new WindingDownOrchestrator();
        using var vm = StartAnImplementationRun(orchestrator, new FakeTaskStateStore());

        var command = Assert.IsAssignableFrom<IAsyncRelayCommand>(vm.CompleteTaskCommand);
        Assert.True(command.CanExecute(null));

        var closes = 0;
        vm.CloseRequested += (_, _) => closes++;

        var first = command.ExecuteAsync(null);

        // Still awaiting the run - the run has not ended and nothing has closed yet.
        Assert.False(command.CanExecute(null));
        Assert.False(orchestrator.HasEnded);
        Assert.Equal(0, closes);

        await first;

        Assert.Equal(1, closes);

        // The run has ended, so the action is gone for good.
        Assert.False(command.CanExecute(null));
    }

    [WpfFact]
    public async Task CompleteTask_IsNotOfferedOnceTheRunHasEnded()
    {
        var orchestrator = new WindingDownOrchestrator();
        using var vm = StartAnImplementationRun(orchestrator, new FakeTaskStateStore());

        Assert.True(vm.CompleteTaskCommand.CanExecute(null));

        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        vm.CompleteTaskCommand.Execute(null);
        await Settle(() => closed, "the task completion never requested the tab close");

        Assert.False(vm.IsRunning);
        Assert.False(vm.CompleteTaskCommand.CanExecute(null));
    }

    // -----------------------------------------------------------------------------------------
    // Requirement 5.4, recovery half: the task does not come back. This is the end-to-end
    // property, not the two journal fields - an assertion that only read the fields would still
    // pass a change that broke reconciliation, and reconciliation is the only consumer that
    // decides whether the next startup offers this task again.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Both halves of design decision 6b, checked through the component that actually reads them.
    /// The ledger staged here is deliberately NOT all-complete, so the automatic rule would demote
    /// Implementation; only the recorded <see cref="PhaseStatus.Completed"/> plus the override flag
    /// can make <see cref="PhaseReconciliation.FirstIncomplete"/> return null. Recording the flag
    /// alone leaves the entry <see cref="PhaseStatus.Active"/>, and
    /// <see cref="PhaseReconciliation.Reconcile"/> never even looks at the flag for an entry that is
    /// not already Completed - which is why the flag on its own is not requirement 5.4.
    /// </summary>
    [WpfFact]
    public async Task CompleteTask_LeavesNothingForRecoveryToResume()
    {
        var paths = new TaskPaths(_workspace, TaskName);
        WriteEarlierPhaseArtefacts(paths);

        // Four entries, one complete: positively unfinished work, which is the one case that demotes.
        var tracking = WriteIncompleteLedger();

        var orchestrator = new WindingDownOrchestrator();
        var store = new FakeTaskStateStore();
        using var vm = StartAnImplementationRun(orchestrator, store);

        // What the orchestrator itself writes on a real subtask run: the configuration at phase
        // entry, and an Implementation entry that stays Active because the ledger never came back
        // all-complete (WorkflowOrchestrator.CompleteImplementationIfEveryEntryIsComplete).
        store.SaveSubtaskSettings(paths, enabled: true, _tracking);
        store.RecordPhase(paths, WorkflowPhase.Specification, PhaseStatus.Completed);
        store.RecordPhase(paths, WorkflowPhase.Review, PhaseStatus.Completed);
        store.RecordPhase(paths, WorkflowPhase.ResolveReview, PhaseStatus.Completed);
        store.RecordPhase(paths, WorkflowPhase.Implementation, PhaseStatus.Active);

        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        vm.CompleteTaskCommand.Execute(null);

        await Settle(() => closed, "the task completion never requested the tab close");

        // The staged evidence really is unfinished, so the assertions below cannot be satisfied by
        // the automatic all-complete rule.
        var snapshot = SubtaskLedger.TryRead(tracking);
        Assert.NotNull(snapshot);
        Assert.NotEqual(snapshot.Total, snapshot.Completed);

        var state = store.TryLoad(paths);
        Assert.NotNull(state);

        // Design decision 6b, both halves.
        Assert.Equal(
            PhaseStatus.Completed,
            state.Phases.Single(entry => entry.Phase == WorkflowPhase.Implementation).Status);
        Assert.True(state.ImplementationCompletedManually);

        // Requirement 5.4 as recovery observes it: nothing is demoted and nothing is left to resume.
        Assert.False(PhaseReconciliation.Reconcile(paths, state), "reconciliation demoted a manually completed task");
        Assert.Equal(SubtaskEvidence.CompletedManually, PhaseReconciliation.Evaluate(paths, state));
        Assert.Null(PhaseReconciliation.FirstIncomplete(state));
    }

    /// <summary>
    /// The write ORDER inside <c>CompleteTaskAsync</c>, which no other fixture in this class can
    /// observe. <see cref="TaskStateStore.RecordPhase"/> creates the journal when none exists
    /// (<c>TryLoad(paths) ?? CreateEmpty()</c>), while
    /// <see cref="TaskStateStore.SetImplementationCompletedManually"/> is a documented no-op without
    /// one. Every other test here reaches completion through <c>StartWorkflow</c>, whose
    /// <c>SaveDescription</c> has already created the journal, so both orders leave identical state
    /// and the deliberate ordering is unpinned. The journal is therefore removed here after the run
    /// has started - the state in which the two orders differ - and the override must survive it.
    /// The real <see cref="TaskStateStore"/> is used rather than a fake, because the asymmetry being
    /// relied on is the production store's own.
    /// </summary>
    [WpfFact]
    public async Task CompleteTask_WithoutAnExistingJournal_StillRecordsTheManualOverride()
    {
        var orchestrator = new WindingDownOrchestrator();
        var store = new TaskStateStore();
        using var vm = StartAnImplementationRun(orchestrator, store);

        var paths = new TaskPaths(_workspace, TaskName);

        // Positive control: the run really did write a journal, so deleting it really does produce
        // the journal-less state - the assertion below is not passing for want of a fixture.
        Assert.NotNull(store.TryLoad(paths));
        File.Delete(paths.StateAbsolute);
        Assert.Null(store.TryLoad(paths));

        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        vm.CompleteTaskCommand.Execute(null);

        await Settle(() => closed, "the task completion never requested the tab close");

        var state = store.TryLoad(paths);
        Assert.NotNull(state);

        // Passes in either order - RecordPhase creates the journal wherever it runs.
        Assert.Equal(
            PhaseStatus.Completed,
            state.Phases.Single(entry => entry.Phase == WorkflowPhase.Implementation).Status);

        // Fails in the other order: the override would have been dropped on the floor.
        Assert.True(
            state.ImplementationCompletedManually,
            "the manual override was written before the journal that holds it existed");
    }

    /// <summary>Writes the artefacts the three earlier phases are reconciled against.</summary>
    private static void WriteEarlierPhaseArtefacts(TaskPaths paths)
    {
        Directory.CreateDirectory(paths.TaskDirectory);
        File.WriteAllText(paths.SpecAbsolute, "Spezifikation");
        File.WriteAllText(paths.PlanAbsolute, "Plan");
        File.WriteAllText(paths.ReviewAbsolute, "Review");
    }

    /// <summary>Stages an ordered index whose entries are not all complete.</summary>
    private SubtaskPaths WriteIncompleteLedger()
    {
        var tracking = new SubtaskPaths(_tracking, TaskName);
        Directory.CreateDirectory(tracking.TaskDirectory);

        string[] titles = ["ST-001-lesen", "ST-002-pruefen", "ST-003-schreiben", "ST-004-liefern"];
        File.WriteAllText(
            tracking.ResultAbsolute,
            $$"""{ "version": 1, "subtasks": {{JsonSerializer.Serialize(titles)}} }""");

        for (var i = 0; i < titles.Length; i++)
        {
            var title = titles[i];
            Directory.CreateDirectory(tracking.SubtaskDirectory(title));
            File.WriteAllText(tracking.SubtaskMarkdown(title), $"Beschreibung von {title}");

            if (i == 0)
            {
                File.WriteAllText(tracking.SubtaskStatusFile(title), """{ "status": "complete" }""");
            }
        }

        return tracking;
    }
}
