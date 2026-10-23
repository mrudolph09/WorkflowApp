using System.IO;
using System.Windows.Threading;
using Workflow.Models;
using Workflow.Services;
using Workflow.Terminal;
using Workflow.Tests.Fakes;
using Workflow.ViewModels;

namespace Workflow.Tests;

/// <remarks>
/// Workflow_LoadFix spec section 8: 'Implementierung beginnen' re-reconciles the journal with the
/// disk, records Vereinfachen, Review and Review umsetzen as skipped where they are not completed in
/// one verified write, and starts the implementation through the ordinary start path. It never raises
/// the manual signal (subtask requirement 5.2).
/// </remarks>
public sealed class TaskTabStartImplementationTests : IDisposable
{
    private const string TaskName = "alpha";

    private const string SkipNotRecordedMessage =
        "Implementierung nicht gestartet: Das Überspringen der Review konnte nicht in .workflow-state.json gespeichert werden.";

    private const string SpecOrPlanMissingMessage =
        "Implementierung nicht gestartet: Spezifikation oder Plan fehlt oder ist leer. Nächste Phase: Spezifikation.";

    private const string RestartQuestion =
        "Task komplett von vorn beginnen? Spezifikation und alle Phasen werden neu ausgeführt.";

    private const string FinishedTaskMessage =
        "Alle Phasen sind abgeschlossen. 'Start workflow' beginnt den Task von vorn.";

    private const string ReopenImplementationMessage =
        "Implementierung wurde per 'Task abschliessen' beendet, Subtasks sind noch offen. 'Continue workflow' setzt die Implementierung fort.";

    private static readonly DateTimeOffset Stamp = new(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);

    private readonly string _root;
    private readonly string _tracking;
    private readonly SettingsService _settings;
    private readonly FakeTaskStateStore _store = new();

    public TaskTabStartImplementationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wf-skip-" + Guid.NewGuid().ToString("N"));
        _tracking = Path.Combine(_root, "workflows");
        Directory.CreateDirectory(_root);
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

    private TaskPaths Paths => new(_root, TaskName);

    // CA2000 cannot trace either disposal: WebViewEnvironmentProvider is a deliberate app-wide
    // singleton, and TerminalViewModel's ownership passes into the returned tab, whose Dispose()
    // disposes it.
#pragma warning disable CA2000
    private TaskTabViewModel Create(
        IWorkflowOrchestrator orchestrator,
        IReadOnlyList<string>? startupErrors = null,
        ITaskStateStore? store = null,
        IMessageDialogService? messages = null) => new(
        new TaskFolderService(),
        orchestrator,
        _settings,
        store ?? _store,
        new StubDirectoryPicker(),
        new TerminalViewModel(
            new WebViewEnvironmentProvider(),
            new ConPtySessionFactory(),
            Dispatcher.CurrentDispatcher),
        folderDebounce: TimeSpan.Zero,
        startupErrors: startupErrors ?? [],
        messages: messages);
#pragma warning restore CA2000

    private sealed class RecordingMessages(params bool[] answers) : IMessageDialogService
    {
        private readonly Queue<bool> _answers = new(answers);

        public List<string> Questions { get; } = [];

        public void ShowError(string message) => throw new InvalidOperationException(message);

        /// <summary>Returns the queued answers in order, then false ("Nein").</summary>
        public bool Confirm(string message)
        {
            Questions.Add(message);
            return _answers.Count > 0 && _answers.Dequeue();
        }
    }

    /// <summary>Records every run; each one lasts until it is cancelled.</summary>
    private sealed class RecordingOrchestrator : IWorkflowOrchestrator
    {
        public List<WorkflowRunRequest> Requests { get; } = [];

        public List<Task> Runs { get; } = [];

        public Task RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var run = Task.Delay(Timeout.Infinite, cancellationToken);
            Runs.Add(run);
            return run;
        }
    }

    /// <summary>A first run that, once cancelled, keeps winding down until the test releases it.</summary>
    private sealed class WindingDownOrchestrator : IWorkflowOrchestrator
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<WorkflowRunRequest> Requests { get; } = [];

        public void Release() => _release.TrySetResult();

        public Task RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Requests.Count == 1
                ? WindDownAsync(cancellationToken)
                : Task.Delay(Timeout.Infinite, cancellationToken);
        }

        private async Task WindDownAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await _release.Task;
                throw;
            }
        }
    }

    private sealed class StubDirectoryPicker : IDirectoryPickerService
    {
        public string? PickDirectory(string? initialDirectory, string title = IDirectoryPickerService.DefaultTitle) => null;
    }

    // Written before Stamp, so the fresh reconciliation of 'Implementierung beginnen' finds every
    // completed phase backed and promotes nothing the journal does not already say.
    private static void WriteArtefact(string path, TimeSpan beforeStamp)
    {
        File.WriteAllText(path, "inhalt");
        File.SetLastWriteTimeUtc(path, (Stamp - beforeStamp).UtcDateTime);
    }

    private static void WriteSpecAndPlan(TaskPaths paths)
    {
        WriteArtefact(paths.SpecAbsolute, TimeSpan.FromMinutes(10));
        WriteArtefact(paths.PlanAbsolute, TimeSpan.FromMinutes(10));
    }

    /// <summary>
    /// Journals the five statuses in catalogue order, writes the artefacts of every completed phase,
    /// and opens the task in the tab, as 'Task laden' does.
    /// </summary>
    private void Load(TaskTabViewModel vm, WorkflowPhase? resume, params PhaseStatus[] statuses)
    {
        var paths = Paths;
        Directory.CreateDirectory(paths.TaskDirectory);

        var state = new TaskState { TaskDescription = "d", UpdatedUtc = Stamp };
        for (var i = 0; i < PhaseCatalog.All.Count; i++)
        {
            state.Phases.Add(new TaskPhaseState(
                PhaseCatalog.All[i].Phase,
                statuses[i],
                statuses[i] is PhaseStatus.Completed or PhaseStatus.Skipped ? Stamp : null));
        }

        // Without the artefacts the fresh reconciliation would demote phase 1 (spec 8.3 step 4).
        if (statuses[(int)WorkflowPhase.Specification] == PhaseStatus.Completed)
        {
            WriteSpecAndPlan(paths);
        }

        if (statuses[(int)WorkflowPhase.Simplifier] == PhaseStatus.Completed)
        {
            WriteArtefact(paths.SimplifiedAbsolute, TimeSpan.FromMinutes(7));
        }

        if (statuses[(int)WorkflowPhase.Review] == PhaseStatus.Completed)
        {
            WriteArtefact(paths.ReviewAbsolute, TimeSpan.FromMinutes(5));
        }

        if (statuses[(int)WorkflowPhase.Implementation] == PhaseStatus.Completed)
        {
            WriteArtefact(paths.DoneAbsolute, TimeSpan.FromMinutes(1));
        }

        _store.Seed(paths, state);
        vm.LoadForResume(new RecoverableTask(paths, state, resume));
    }

    /// <summary>
    /// Opens a finished subtask task as 'Task laden' does: all five phases Completed, subtask mode
    /// on, the manual-completion flag as given, and a ledger whose first <paramref name="complete"/>
    /// of <paramref name="total"/> entries are complete. No index at all when the total is zero.
    /// <paramref name="reviewsSkipped"/> records Vereinfachen, Review and Review umsetzen as Skipped
    /// instead - the shape an earlier 'Implementierung beginnen' leaves behind.
    /// </summary>
    private void LoadFinishedSubtaskTask(
        TaskTabViewModel vm, bool completedManually, int complete, int total, bool reviewsSkipped = false)
    {
        var paths = Paths;
        Directory.CreateDirectory(paths.TaskDirectory);
        WriteSpecAndPlan(paths);
        WriteArtefact(paths.SimplifiedAbsolute, TimeSpan.FromMinutes(7));
        WriteArtefact(paths.ReviewAbsolute, TimeSpan.FromMinutes(5));

        if (total > 0)
        {
            WriteLedger(complete, total);
        }

        var state = new TaskState
        {
            TaskDescription = "d",
            UpdatedUtc = Stamp,
            SubtasksEnabled = true,
            WorkflowDirectory = _tracking,
            ImplementationCompletedManually = completedManually,
        };

        foreach (var definition in PhaseCatalog.All)
        {
            var skipped = reviewsSkipped
                && definition.Phase is WorkflowPhase.Simplifier or WorkflowPhase.Review or WorkflowPhase.ResolveReview;
            state.Phases.Add(new TaskPhaseState(definition.Phase, skipped ? PhaseStatus.Skipped : PhaseStatus.Completed, Stamp));
        }

        _store.Seed(paths, state);
        vm.LoadForResume(new RecoverableTask(paths, state, null));
    }

    private void WriteLedger(int complete, int total)
    {
        var tracking = new SubtaskPaths(_tracking, TaskName);
        Directory.CreateDirectory(tracking.TaskDirectory);

        var titles = Enumerable.Range(1, total).Select(i => $"ST-{i:000}-eintrag").ToArray();
        File.WriteAllText(
            tracking.ResultAbsolute,
            $$"""{ "version": 1, "subtasks": {{System.Text.Json.JsonSerializer.Serialize(titles)}} }""");

        for (var i = 0; i < titles.Length; i++)
        {
            Directory.CreateDirectory(tracking.SubtaskDirectory(titles[i]));
            File.WriteAllText(tracking.SubtaskMarkdown(titles[i]), $"Beschreibung von {titles[i]}");
            File.WriteAllText(
                tracking.SubtaskStatusFile(titles[i]),
                i < complete ? """{ "status": "complete" }""" : """{ "status": "pending" }""");
        }
    }

    // --- Availability (spec 8.2) ------------------------------------------------------------

    [Fact]
    public void StartImplementation_FreshTab_IsNotOffered()
    {
        // Arrange - phase 1 never ran, so there is no spec and plan to implement.
        using var vm = Create(new RecordingOrchestrator());
        vm.WorkingDirectory = _root;
        vm.TaskName = TaskName;

        // Act
        var offered = vm.StartImplementationCommand.CanExecute(null);

        // Assert
        Assert.True(vm.StartWorkflowCommand.CanExecute(null));   // positive control: the gates are open
        Assert.False(offered);
    }

    [Fact]
    public void StartImplementation_LoadedTabAtReview_IsOffered()
    {
        // Arrange
        using var vm = Create(new RecordingOrchestrator());

        // Act
        Load(vm, WorkflowPhase.Review, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending);

        // Assert
        Assert.True(vm.StartImplementationCommand.CanExecute(null));
    }

    [Fact]
    public void StartImplementation_ImplementationCompletedNormally_IsNotOffered()
    {
        // Arrange - a normal (non-subtask) task that finished on its own: nothing to continue.
        using var vm = Create(new RecordingOrchestrator());

        // Act
        Load(vm, null, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Completed);

        // Assert
        Assert.True(vm.StartWorkflowCommand.CanExecute(null));   // positive control: Start restarts it
        Assert.False(vm.StartImplementationCommand.CanExecute(null));
    }

    [Fact]
    public void StartImplementation_ManuallyCompletedWithOpenSubtasks_IsOffered()
    {
        // Arrange - 'Task abschliessen' at 1 of 3: the task can be reopened at phase 4.
        using var vm = Create(new RecordingOrchestrator());

        // Act
        LoadFinishedSubtaskTask(vm, completedManually: true, complete: 1, total: 3);

        // Assert
        Assert.True(vm.StartImplementationCommand.CanExecute(null));
    }

    [Fact]
    public void StartImplementation_ManuallyCompletedWithAllSubtasksComplete_IsNotOffered()
    {
        // Arrange
        using var vm = Create(new RecordingOrchestrator());

        // Act
        LoadFinishedSubtaskTask(vm, completedManually: true, complete: 3, total: 3);

        // Assert
        Assert.False(vm.StartImplementationCommand.CanExecute(null));
    }

    [Fact]
    public void StartImplementation_CompletedWithoutTheManualFlag_IsNotOffered()
    {
        // Arrange - the open entries are not what decides: only a manual completion is reopened.
        using var vm = Create(new RecordingOrchestrator());

        // Act
        LoadFinishedSubtaskTask(vm, completedManually: false, complete: 1, total: 3);

        // Assert
        Assert.False(vm.StartImplementationCommand.CanExecute(null));
    }

    [Fact]
    public void StartImplementation_ManuallyCompletedWithoutAReadableIndex_IsNotOffered()
    {
        // Arrange - no index: nothing says any work is left (requirement 4.6).
        using var vm = Create(new RecordingOrchestrator());

        // Act
        LoadFinishedSubtaskTask(vm, completedManually: true, complete: 0, total: 0);

        // Assert
        Assert.False(vm.StartImplementationCommand.CanExecute(null));
    }

    [WpfFact]
    public async Task StartImplementation_ManuallyCompletedWithOpenSubtasks_ClearsTheFlagAndContinuesAtImplementation()
    {
        // Arrange
        var orchestrator = new RecordingOrchestrator();
        var messages = new RecordingMessages();
        using var vm = Create(orchestrator, messages: messages);
        LoadFinishedSubtaskTask(vm, completedManually: true, complete: 1, total: 3);

        // Act
        await vm.StartImplementationCommand.ExecuteAsync(null);

        // Assert - the run continues at the implementation and asks nothing.
        var request = Assert.Single(orchestrator.Requests);
        Assert.Equal(WorkflowPhase.Implementation, request.StartPhase);
        Assert.Empty(messages.Questions);

        var journal = _store.TryLoad(Paths);
        Assert.NotNull(journal);
        Assert.False(journal.ImplementationCompletedManually);

        // Cleared before the reconciliation, which therefore demoted the implementation off the open ledger.
        Assert.Equal(PhaseStatus.Pending, journal.Phases[(int)WorkflowPhase.Implementation].Status);

        // Every earlier phase is untouched: still Completed, with its original stamp.
        for (var i = 0; i < (int)WorkflowPhase.Implementation; i++)
        {
            Assert.Equal(PhaseStatus.Completed, journal.Phases[i].Status);
            Assert.Equal(Stamp, journal.Phases[i].CompletedUtc);
        }

        Assert.Equal(
            new[] { PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Pending },
            vm.Phases.Select(p => p.Status));
    }

    // --- A task completed by hand with open subtasks is continued, not restarted --------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadForResume_ManuallyCompletedWithOpenSubtasks_OffersContinueWorkflow(bool reviewsSkipped)
    {
        // Arrange
        using var vm = Create(new RecordingOrchestrator());

        // Act
        LoadFinishedSubtaskTask(vm, completedManually: true, complete: 17, total: 48, reviewsSkipped);

        // Assert - the main button points at the continuation, not at a restart.
        Assert.Equal("Continue workflow", vm.StartButtonLabel);
        Assert.Equal(ReopenImplementationMessage, vm.InfoMessage);
        Assert.True(vm.StartWorkflowCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(true, 3, 3)]    // every subtask complete: really finished
    [InlineData(false, 1, 3)]   // finished on its own: only a manual completion is reopened
    public void LoadForResume_FinishedSubtaskTask_StillOffersARestart(bool completedManually, int complete, int total)
    {
        // Arrange
        using var vm = Create(new RecordingOrchestrator());

        // Act
        LoadFinishedSubtaskTask(vm, completedManually, complete, total);

        // Assert
        Assert.Equal("Start workflow", vm.StartButtonLabel);
        Assert.Equal(FinishedTaskMessage, vm.InfoMessage);
    }

    [WpfFact]
    public async Task StartWorkflow_ManuallyCompletedWithOpenSubtasks_ContinuesAtImplementationWithoutAsking()
    {
        // Arrange - MP_START's shape: the reviews were skipped, 'Task abschliessen' at 17 of 48.
        var orchestrator = new RecordingOrchestrator();
        var messages = new RecordingMessages();
        using var vm = Create(orchestrator, messages: messages);
        LoadFinishedSubtaskTask(vm, completedManually: true, complete: 17, total: 48, reviewsSkipped: true);

        // Act
        vm.StartWorkflowCommand.Execute(null);
        if (vm.StartImplementationCommand.ExecutionTask is { } switching)
        {
            await switching;
        }

        // Assert - no restart question, the run picks up the implementation.
        Assert.Empty(messages.Questions);
        Assert.Equal(WorkflowPhase.Implementation, Assert.Single(orchestrator.Requests).StartPhase);

        var journal = _store.TryLoad(Paths);
        Assert.NotNull(journal);
        Assert.False(journal.ImplementationCompletedManually);
        Assert.Equal(PhaseStatus.Pending, journal.Phases[(int)WorkflowPhase.Implementation].Status);

        // Specification and the skipped phases keep their entries.
        Assert.Equal(PhaseStatus.Completed, journal.Phases[(int)WorkflowPhase.Specification].Status);
        Assert.Equal(PhaseStatus.Skipped, journal.Phases[(int)WorkflowPhase.Simplifier].Status);
        Assert.Equal(PhaseStatus.Skipped, journal.Phases[(int)WorkflowPhase.Review].Status);
        Assert.Equal(PhaseStatus.Skipped, journal.Phases[(int)WorkflowPhase.ResolveReview].Status);
    }

    [WpfFact]
    public void StartWorkflow_OpenSubtasksFinishedSinceLoading_StartsNothingAndOffersARestart()
    {
        // Arrange - the label promised a continuation, then the last subtasks completed on disk.
        var orchestrator = new RecordingOrchestrator();
        var messages = new RecordingMessages();
        using var vm = Create(orchestrator, messages: messages);
        LoadFinishedSubtaskTask(vm, completedManually: true, complete: 1, total: 3);
        Assert.Equal("Continue workflow", vm.StartButtonLabel);   // positive control
        WriteLedger(3, 3);

        // Act
        vm.StartWorkflowCommand.Execute(null);

        // Assert - a click on 'Continue workflow' never turns into a restart; the button now says what it does.
        Assert.Empty(messages.Questions);
        Assert.Empty(orchestrator.Requests);
        Assert.False(vm.IsRunning);
        Assert.Equal("Start workflow", vm.StartButtonLabel);
        Assert.Equal(FinishedTaskMessage, vm.InfoMessage);
    }

    // --- Restarting a finished task asks first ------------------------------------------------

    [WpfFact]
    public void StartWorkflow_FinishedTaskAndTheAnswerIsNo_StartsNothingAndLeavesTheJournal()
    {
        // Arrange
        var orchestrator = new RecordingOrchestrator();
        var messages = new RecordingMessages(false);
        using var vm = Create(orchestrator, messages: messages);
        Load(vm, null, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Completed);

        // Act
        vm.StartWorkflowCommand.Execute(null);

        // Assert
        Assert.Equal(RestartQuestion, Assert.Single(messages.Questions));
        Assert.Empty(orchestrator.Requests);
        Assert.False(vm.IsRunning);
        Assert.Empty(_store.Descriptions);
        var journal = _store.TryLoad(Paths);
        Assert.NotNull(journal);
        Assert.All(journal.Phases, p => Assert.Equal(PhaseStatus.Completed, p.Status));
        Assert.All(vm.Phases, p => Assert.Equal(PhaseStatus.Completed, p.Status));
    }

    // WF_QUEUE spec Q9 / AC18: CanExecute before the call is not evidence of a start - the restart question
    // can still be answered "Nein" inside StartWorkflow, so the return value reads IsRunning afterwards.
    [WpfFact]
    public void StartIfQueuedAfter_AllGreenSuccessorAnsweredNo_ReturnsFalseAndSaysSo()
    {
        // Arrange
        var orchestrator = new RecordingOrchestrator();
        var messages = new RecordingMessages(false);
        using var predecessor = Create(new RecordingOrchestrator());
        predecessor.TaskName = "alpha";
        using var vm = Create(orchestrator, messages: messages);
        Load(vm, null, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Completed);
        vm.QueuedAfter = predecessor;
        Assert.True(vm.StartWorkflowCommand.CanExecute(null));

        // Act
        var started = vm.StartIfQueuedAfter(predecessor);

        // Assert
        Assert.False(started);
        Assert.Equal(RestartQuestion, Assert.Single(messages.Questions));
        Assert.Empty(orchestrator.Requests);
        Assert.False(vm.IsRunning);
        Assert.Null(vm.QueuedAfter);
        Assert.Equal("Queue: 'alpha' ist fertig, der Workflow konnte aber nicht automatisch gestartet werden.", vm.InfoMessage);
    }

    [WpfFact]
    public void StartWorkflow_FinishedTaskAndTheAnswerIsYes_RestartsAtTheSpecification()
    {
        // Arrange
        var orchestrator = new RecordingOrchestrator();
        var messages = new RecordingMessages(true);
        using var vm = Create(orchestrator, messages: messages);
        Load(vm, null, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Completed);

        // Act
        vm.StartWorkflowCommand.Execute(null);

        // Assert
        Assert.Equal(RestartQuestion, Assert.Single(messages.Questions));
        var request = Assert.Single(orchestrator.Requests);
        Assert.Equal(WorkflowPhase.Specification, request.StartPhase);
        Assert.True(vm.IsRunning);
    }

    [WpfFact]
    public void StartWorkflow_WithAResumePhase_DoesNotAsk()
    {
        // Arrange
        var orchestrator = new RecordingOrchestrator();
        var messages = new RecordingMessages();
        using var vm = Create(orchestrator, messages: messages);
        Load(vm, WorkflowPhase.Review, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending);

        // Act
        vm.StartWorkflowCommand.Execute(null);

        // Assert
        Assert.Empty(messages.Questions);
        Assert.Equal(WorkflowPhase.Review, Assert.Single(orchestrator.Requests).StartPhase);
    }

    [WpfFact]
    public void StartWorkflow_NewTask_DoesNotAsk()
    {
        // Arrange - no journal at all.
        var orchestrator = new RecordingOrchestrator();
        var messages = new RecordingMessages();
        using var vm = Create(orchestrator, messages: messages);
        vm.WorkingDirectory = _root;
        vm.TaskName = TaskName;

        // Act
        vm.StartWorkflowCommand.Execute(null);

        // Assert
        Assert.Empty(messages.Questions);
        Assert.Equal(WorkflowPhase.Specification, Assert.Single(orchestrator.Requests).StartPhase);
    }

    [Fact]
    public void StartImplementation_WhileImplementationRuns_IsNotOffered()
    {
        // Arrange
        var orchestrator = new RecordingOrchestrator();
        using var vm = Create(orchestrator);
        Load(vm, WorkflowPhase.Implementation, PhaseStatus.Completed, PhaseStatus.Skipped, PhaseStatus.Skipped, PhaseStatus.Skipped, PhaseStatus.Pending);
        vm.StartWorkflowCommand.Execute(null);

        // Act
        vm.ApplyProgress(new PhaseProgress(WorkflowPhase.Implementation, PhaseStatus.Active));

        // Assert
        Assert.True(vm.CompleteTaskCommand.CanExecute(null));   // positive control: phase 4's own action
        Assert.False(vm.StartImplementationCommand.CanExecute(null));
    }

    [Fact]
    public void StartImplementation_StartupPromptError_IsNotOffered()
    {
        // Arrange
        using var vm = Create(new RecordingOrchestrator(), ["Die Prompt-Datei 'review_prompt.md' ist leer."]);

        // Act
        Load(vm, WorkflowPhase.Review, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending);

        // Assert
        Assert.False(vm.StartImplementationCommand.CanExecute(null));
    }

    [Fact]
    public void StartImplementation_AnnouncesItsAvailabilityWhenAnIndicatorChanges()
    {
        // Arrange - the indicator subscription is what reaches ApplyJournal and ResetPhaseIndicators.
        using var vm = Create(new RecordingOrchestrator());
        vm.WorkingDirectory = _root;
        vm.TaskName = TaskName;
        var announced = 0;
        vm.StartImplementationCommand.CanExecuteChanged += (_, _) => announced++;

        // Act
        vm.Phases[0].Status = PhaseStatus.Completed;

        // Assert
        Assert.Equal(1, announced);
        Assert.True(vm.StartImplementationCommand.CanExecute(null));
    }

    // --- Execution (spec 8.3) ---------------------------------------------------------------

    [WpfFact]
    public async Task StartImplementation_LoadedTabAtReview_SkipsBothReviewPhasesAndStartsImplementation()
    {
        // Arrange
        var orchestrator = new RecordingOrchestrator();
        using var vm = Create(orchestrator);
        Load(vm, WorkflowPhase.Review, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending);

        // Act
        await vm.StartImplementationCommand.ExecuteAsync(null);

        // Assert
        var request = Assert.Single(orchestrator.Requests);
        Assert.Equal(WorkflowPhase.Implementation, request.StartPhase);
        Assert.True(vm.IsRunning);
        Assert.Equal(
            new[] { PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Skipped, PhaseStatus.Skipped, PhaseStatus.Pending },
            vm.Phases.Select(p => p.Status));
        var journal = _store.TryLoad(Paths);
        Assert.NotNull(journal);
        Assert.Equal(PhaseStatus.Completed, journal.Phases[(int)WorkflowPhase.Simplifier].Status);
        Assert.Equal(PhaseStatus.Skipped, journal.Phases[(int)WorkflowPhase.Review].Status);
        Assert.Equal(PhaseStatus.Skipped, journal.Phases[(int)WorkflowPhase.ResolveReview].Status);
        Assert.NotNull(journal.Phases[(int)WorkflowPhase.Review].CompletedUtc);
    }

    [WpfFact]
    public async Task StartImplementation_LoadedTabAtSimplifier_SkipsTheSimplifierWithBothReviewPhasesAndStartsImplementation()
    {
        // Arrange - only the specification ran; the simplifier is next.
        var orchestrator = new RecordingOrchestrator();
        using var vm = Create(orchestrator);
        Load(vm, WorkflowPhase.Simplifier, PhaseStatus.Completed, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending);

        // Act
        await vm.StartImplementationCommand.ExecuteAsync(null);

        // Assert - one skip request covers all three, with one stamp.
        var request = Assert.Single(orchestrator.Requests);
        Assert.Equal(WorkflowPhase.Implementation, request.StartPhase);
        Assert.Equal(
            new[] { PhaseStatus.Completed, PhaseStatus.Skipped, PhaseStatus.Skipped, PhaseStatus.Skipped, PhaseStatus.Pending },
            vm.Phases.Select(p => p.Status));
        var skip = Assert.Single(_store.SkipRequests);
        Assert.Equal(
            new[] { WorkflowPhase.Simplifier, WorkflowPhase.Review, WorkflowPhase.ResolveReview },
            skip.Phases.Order());
        var journal = _store.TryLoad(Paths);
        Assert.NotNull(journal);
        Assert.Equal(PhaseStatus.Skipped, journal.Phases[(int)WorkflowPhase.Simplifier].Status);
        Assert.Equal(PhaseStatus.Skipped, journal.Phases[(int)WorkflowPhase.Review].Status);
        Assert.Equal(PhaseStatus.Skipped, journal.Phases[(int)WorkflowPhase.ResolveReview].Status);
        Assert.NotNull(journal.Phases[(int)WorkflowPhase.Simplifier].CompletedUtc);
        Assert.Equal(
            journal.Phases[(int)WorkflowPhase.Simplifier].CompletedUtc,
            journal.Phases[(int)WorkflowPhase.ResolveReview].CompletedUtc);
    }

    [WpfFact]
    public async Task StartImplementation_AfterACompletedReview_SkipsOnlyReviewUmsetzen()
    {
        // Arrange - the review happened; only its resolution is skipped (spec I5).
        var orchestrator = new RecordingOrchestrator();
        using var vm = Create(orchestrator);
        Load(vm, WorkflowPhase.ResolveReview, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Pending, PhaseStatus.Pending);

        // Act
        await vm.StartImplementationCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal(PhaseStatus.Completed, vm.Phases[(int)WorkflowPhase.Simplifier].Status);
        Assert.Equal(PhaseStatus.Completed, vm.Phases[(int)WorkflowPhase.Review].Status);
        Assert.Equal(PhaseStatus.Skipped, vm.Phases[(int)WorkflowPhase.ResolveReview].Status);
        var journal = _store.TryLoad(Paths);
        Assert.NotNull(journal);
        Assert.Equal(PhaseStatus.Completed, journal.Phases[(int)WorkflowPhase.Simplifier].Status);
        Assert.Equal(PhaseStatus.Completed, journal.Phases[(int)WorkflowPhase.Review].Status);
        Assert.Equal(PhaseStatus.Skipped, journal.Phases[(int)WorkflowPhase.ResolveReview].Status);
        Assert.Single(_store.SkipRequests);
    }

    [WpfFact]
    public async Task StartImplementation_WithSubtasksTicked_HandsTheRunTheSubtaskConfiguration()
    {
        // Arrange
        var orchestrator = new RecordingOrchestrator();
        using var vm = Create(orchestrator);
        Load(vm, WorkflowPhase.Review, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending);
        vm.SubtasksEnabled = true;
        vm.WorkflowDirectory = _tracking;

        // Act
        await vm.StartImplementationCommand.ExecuteAsync(null);

        // Assert - implementation entry forces the deferred capture at once.
        var request = Assert.Single(orchestrator.Requests);
        Assert.NotNull(request.Subtasks);
        Assert.True(request.Subtasks.Value.Enabled);
        Assert.Equal(_tracking, request.Subtasks.Value.WorkflowDirectory);
    }

    [WpfFact]
    public async Task StartImplementation_WhilePaused_StartsImplementationAndKeepsThePause()
    {
        // Arrange - Review Focus 5: the orchestrator never holds the start phase.
        var orchestrator = new RecordingOrchestrator();
        using var vm = Create(orchestrator);
        Load(vm, WorkflowPhase.Review, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending);
        vm.PauseCommand.Execute(null);

        // Act
        await vm.StartImplementationCommand.ExecuteAsync(null);

        // Assert
        var request = Assert.Single(orchestrator.Requests);
        Assert.Equal(WorkflowPhase.Implementation, request.StartPhase);
        Assert.True(vm.IsPaused);
        Assert.NotNull(request.Pause);
        Assert.True(request.Pause.IsPaused);
    }

    // --- Refusals and the disk (spec 8.3 steps 4-5, AC-11, F22-F24) ---------------------------

    [WpfFact]
    public async Task StartImplementation_SkipWriteFails_StartsNothingAndSaysWhy()
    {
        // Arrange - D19: a skip that did not land must not be followed by an implementation run.
        var orchestrator = new RecordingOrchestrator();
        using var vm = Create(orchestrator);
        Load(vm, WorkflowPhase.Review, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending);
        _store.FailSkipWrites = true;

        // Act
        await vm.StartImplementationCommand.ExecuteAsync(null);

        // Assert
        Assert.Empty(orchestrator.Requests);
        Assert.Equal(SkipNotRecordedMessage, vm.InfoMessage);
        Assert.Equal(
            new[] { PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending },
            vm.Phases.Select(p => p.Status));
        Assert.False(vm.IsRunning);
    }

    [WpfFact]
    public async Task StartImplementation_SpecDeletedAfterLoading_StartsNothingAndNamesTheNextPhase()
    {
        // Arrange - D18: the indicators still say phase 1 is done; the disk no longer does.
        var orchestrator = new RecordingOrchestrator();
        using var vm = Create(orchestrator);
        Load(vm, WorkflowPhase.Review, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending);
        File.Delete(Paths.SpecAbsolute);

        // Act
        await vm.StartImplementationCommand.ExecuteAsync(null);

        // Assert
        Assert.Empty(orchestrator.Requests);
        Assert.Empty(_store.SkipRequests);
        Assert.Equal(SpecOrPlanMissingMessage, vm.InfoMessage);
        Assert.All(vm.Phases, p => Assert.Equal(PhaseStatus.Pending, p.Status));
    }

    [WpfFact]
    public async Task StartImplementation_PlanEmptiedAfterLoading_StartsNothingAndNamesTheNextPhase()
    {
        // Arrange - a 0-byte plan is not present (spec 4.3).
        var orchestrator = new RecordingOrchestrator();
        using var vm = Create(orchestrator);
        Load(vm, WorkflowPhase.Review, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending);
        await File.WriteAllTextAsync(Paths.PlanAbsolute, string.Empty);

        // Act
        await vm.StartImplementationCommand.ExecuteAsync(null);

        // Assert
        Assert.Empty(orchestrator.Requests);
        Assert.Empty(_store.SkipRequests);
        Assert.Equal(SpecOrPlanMissingMessage, vm.InfoMessage);
        Assert.All(vm.Phases, p => Assert.Equal(PhaseStatus.Pending, p.Status));
    }

    [WpfFact]
    public async Task StartImplementation_SpecRewrittenAfterLoading_StillStartsImplementation()
    {
        // Arrange - a rewritten, non-empty spec is still a valid spec.
        var orchestrator = new RecordingOrchestrator();
        using var vm = Create(orchestrator);
        Load(vm, WorkflowPhase.Review, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending);
        await File.WriteAllTextAsync(Paths.SpecAbsolute, "neuer inhalt");

        // Act
        await vm.StartImplementationCommand.ExecuteAsync(null);

        // Assert
        var request = Assert.Single(orchestrator.Requests);
        Assert.Equal(WorkflowPhase.Implementation, request.StartPhase);
    }

    [WpfFact]
    public async Task StartImplementation_PlanLockedWhileSwitching_StillStartsImplementation()
    {
        // Arrange - a lock is transient: locked is not missing (spec 4.3).
        var orchestrator = new RecordingOrchestrator();
        using var vm = Create(orchestrator);
        Load(vm, WorkflowPhase.Review, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending);

        // Act
        using (new FileStream(Paths.PlanAbsolute, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await vm.StartImplementationCommand.ExecuteAsync(null);
        }

        // Assert
        var request = Assert.Single(orchestrator.Requests);
        Assert.Equal(WorkflowPhase.Implementation, request.StartPhase);
    }

    [WpfFact]
    public async Task StartImplementation_ReviewCompletedOnDiskByAnotherWriter_KeepsItCompleted()
    {
        // Arrange - F24 at tab level, with the real store: the journal on disk, not the tab's
        // indicators, decides which review phases still need a skip.
        var store = new TaskStateStore();
        var orchestrator = new RecordingOrchestrator();
        using var vm = Create(orchestrator, store: store);
        var paths = Paths;
        Directory.CreateDirectory(paths.TaskDirectory);
        WriteSpecAndPlan(paths);
        store.SaveDescription(paths, "d");
        store.RecordPhase(paths, WorkflowPhase.Specification, PhaseStatus.Completed);
        vm.LoadForResume(new RecoverableTask(paths, store.TryLoad(paths)!, WorkflowPhase.Simplifier));   // SaveDescription just wrote the journal

        // Another writer completes the review after the tab was loaded.
        await File.WriteAllTextAsync(paths.ReviewAbsolute, "review");
        store.RecordPhase(paths, WorkflowPhase.Review, PhaseStatus.Completed);

        // Act
        await vm.StartImplementationCommand.ExecuteAsync(null);

        // Assert
        var journal = store.TryLoad(paths);
        Assert.NotNull(journal);
        Assert.Equal(PhaseStatus.Skipped, journal.Phases[(int)WorkflowPhase.Simplifier].Status);
        Assert.Equal(PhaseStatus.Completed, journal.Phases[(int)WorkflowPhase.Review].Status);
        Assert.Equal(PhaseStatus.Skipped, journal.Phases[(int)WorkflowPhase.ResolveReview].Status);
        var request = Assert.Single(orchestrator.Requests);
        Assert.Equal(WorkflowPhase.Implementation, request.StartPhase);
    }

    [WpfFact]
    public async Task StartImplementation_TypedTabWithPhaseOneDone_SkipsSimplifierAndReviewsAndStartsImplementation()
    {
        // Arrange - spec 8.4: a typed (not loaded) tab re-armed from its journal works too.
        var paths = Paths;
        Directory.CreateDirectory(paths.TaskDirectory);
        WriteSpecAndPlan(paths);
        var state = new TaskState { TaskDescription = "d", UpdatedUtc = Stamp };
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.Specification, PhaseStatus.Completed, Stamp));
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.Simplifier, PhaseStatus.Pending, null));
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.Review, PhaseStatus.Pending, null));
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.ResolveReview, PhaseStatus.Pending, null));
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.Implementation, PhaseStatus.Pending, null));
        _store.Seed(paths, state);

        var orchestrator = new RecordingOrchestrator();
        using var vm = Create(orchestrator);
        vm.WorkingDirectory = _root;
        vm.TaskName = TaskName;
        Assert.True(vm.StartImplementationCommand.CanExecute(null));

        // Act
        await vm.StartImplementationCommand.ExecuteAsync(null);

        // Assert
        var request = Assert.Single(orchestrator.Requests);
        Assert.Equal(WorkflowPhase.Implementation, request.StartPhase);
        var journal = _store.TryLoad(paths);
        Assert.NotNull(journal);
        Assert.Equal(PhaseStatus.Skipped, journal.Phases[(int)WorkflowPhase.Simplifier].Status);
        Assert.Equal(PhaseStatus.Skipped, journal.Phases[(int)WorkflowPhase.Review].Status);
        Assert.Equal(PhaseStatus.Skipped, journal.Phases[(int)WorkflowPhase.ResolveReview].Status);
    }

    // --- From a running run (spec 8.3 steps 1-3 and 7, 8.4, D13) ----------------------------

    [WpfFact]
    public async Task StartImplementation_WhileTheReviewRuns_CancelsThatRunWithoutTheManualSignalAndStartsImplementation()
    {
        // Arrange
        var orchestrator = new RecordingOrchestrator();
        using var vm = Create(orchestrator);
        Load(vm, WorkflowPhase.Review, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending);
        vm.StartWorkflowCommand.Execute(null);
        vm.ApplyProgress(new PhaseProgress(WorkflowPhase.Review, PhaseStatus.Active));
        Assert.True(vm.StartImplementationCommand.CanExecute(null));   // offered while the review runs

        // Act
        await vm.StartImplementationCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal(2, orchestrator.Requests.Count);
        Assert.Equal(WorkflowPhase.Review, orchestrator.Requests[0].StartPhase);
        Assert.True(orchestrator.Runs[0].IsCanceled);
        Assert.False(orchestrator.Requests[0].ManualSignal.WaitAsync(CancellationToken.None).IsCompleted);
        Assert.Equal(WorkflowPhase.Implementation, orchestrator.Requests[1].StartPhase);
        Assert.True(vm.IsRunning);
        Assert.Equal(PhaseStatus.Completed, vm.Phases[(int)WorkflowPhase.Simplifier].Status);
        Assert.Equal(PhaseStatus.Skipped, vm.Phases[(int)WorkflowPhase.Review].Status);
        Assert.Equal(PhaseStatus.Skipped, vm.Phases[(int)WorkflowPhase.ResolveReview].Status);
    }

    [WpfFact]
    public async Task StartImplementation_TabDisposedWhileTheRunWindsDown_StartsNothing()
    {
        // Arrange
        var orchestrator = new WindingDownOrchestrator();
        var vm = Create(orchestrator);
        Load(vm, WorkflowPhase.Review, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending);
        vm.StartWorkflowCommand.Execute(null);
        vm.ApplyProgress(new PhaseProgress(WorkflowPhase.Review, PhaseStatus.Active));

        // Act
        var switching = vm.StartImplementationCommand.ExecuteAsync(null);
        vm.Dispose();
        orchestrator.Release();
        await switching;

        // Assert
        Assert.Single(orchestrator.Requests);
        Assert.False(vm.IsRunning);
    }

    [WpfFact]
    public async Task StartWorkflow_IsShutAtTheMomentTheCancelledRunEnds()
    {
        // Arrange - spec D13: the cancelled run's finally drops IsRunning before implementation starts; a
        // click on Start in that moment must find the button dead.
        var orchestrator = new RecordingOrchestrator();
        using var vm = Create(orchestrator);
        Load(vm, WorkflowPhase.Review, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending);
        vm.StartWorkflowCommand.Execute(null);
        vm.ApplyProgress(new PhaseProgress(WorkflowPhase.Review, PhaseStatus.Active));

        bool? startOfferedWhenTheRunEnded = null;
        vm.PropertyChanged += (_, e) =>
        {
            if (string.Equals(e.PropertyName, nameof(TaskTabViewModel.IsRunning), StringComparison.Ordinal)
                && !vm.IsRunning
                && startOfferedWhenTheRunEnded is null)
            {
                startOfferedWhenTheRunEnded = vm.StartWorkflowCommand.CanExecute(null);
            }
        };

        // Act
        await vm.StartImplementationCommand.ExecuteAsync(null);

        // Assert
        Assert.False(startOfferedWhenTheRunEnded);
        Assert.Equal(2, orchestrator.Requests.Count);
    }
}
