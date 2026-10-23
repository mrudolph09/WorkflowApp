using System.ComponentModel;
using System.IO;
using System.Windows.Threading;
using Workflow.Models;
using Workflow.Services;
using Workflow.Terminal;
using Workflow.Tests.Fakes;
using Workflow.ViewModels;

namespace Workflow.Tests;

public sealed class TaskTabViewModelTests : IDisposable
{
    private readonly string _root;
    private readonly SettingsService _settings;

    public TaskTabViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wf-tab-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _settings = new SettingsService(Path.Combine(_root, "settings.json"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // CA2000 cannot trace either disposal here: WebViewEnvironmentProvider is a deliberate
    // app-wide singleton (spec 5.3/Task 11) never disposed per-tab in production either, and
    // TerminalViewModel's ownership passes into the returned TaskTabViewModel, whose own
    // Dispose() disposes it - `using var vm = Create();` at every call site closes the loop.
#pragma warning disable CA2000
    private TaskTabViewModel Create(
        IReadOnlyList<string>? startupErrors = null,
        IWorkflowOrchestrator? orchestrator = null,
        ITaskFolderService? folders = null,
        ITaskStateStore? stateStore = null)
    {
        var terminal = new TerminalViewModel(
            new WebViewEnvironmentProvider(),
            new ConPtySessionFactory(),
            Dispatcher.CurrentDispatcher);

        return new TaskTabViewModel(
            folders ?? new TaskFolderService(),
            orchestrator ?? new StubOrchestrator(),
            _settings,
            stateStore ?? new FakeTaskStateStore(),
            new StubDirectoryPicker(null),
            terminal,
            folderDebounce: TimeSpan.Zero,
            startupErrors ?? []);
    }
#pragma warning restore CA2000

    private sealed class StubOrchestrator : IWorkflowOrchestrator
    {
        public Task RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.Infinite, cancellationToken);
    }

    private sealed class ThrowingOrchestrator(Exception failure) : IWorkflowOrchestrator
    {
        public Task RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken) =>
            Task.FromException(failure);
    }

    /// <summary>
    /// A run that journals every phase before the implementation as completed and the implementation
    /// as given, then returns - as a run
    /// whose done file was written does, or a subtask run that stopped short.
    /// </summary>
    private sealed class JournallingOrchestrator(ITaskStateStore store, PhaseStatus implementation)
        : IWorkflowOrchestrator
    {
        public Task RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken)
        {
            foreach (var definition in PhaseCatalog.All)
            {
                store.RecordPhase(
                    request.Paths,
                    definition.Phase,
                    definition.Phase == WorkflowPhase.Implementation ? implementation : PhaseStatus.Completed);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>A folder service whose rename always fails, as a locked or colliding one would.</summary>
    private sealed class FailingRenameFolderService : ITaskFolderService
    {
        private readonly TaskFolderService _inner = new();

        public TaskNameValidation Validate(string? taskName, string? workingDirectory) =>
            _inner.Validate(taskName, workingDirectory);

        public void EnsureCreated(TaskPaths paths) => _inner.EnsureCreated(paths);

        public bool DirectoryAlreadyExisted(TaskPaths paths) => _inner.DirectoryAlreadyExisted(paths);

        public IReadOnlyList<string> FindMissingPlanningArtefacts(TaskPaths paths) =>
            _inner.FindMissingPlanningArtefacts(paths);

        public void AdoptArtefact(string sourceFile, string artefactPath) =>
            _inner.AdoptArtefact(sourceFile, artefactPath);

        public void Rename(string workingDirectory, string oldName, string newName) =>
            throw new IOException("Der Ordner wird von einem anderen Prozess verwendet.");
    }

    private sealed class StubDirectoryPicker(string? result) : IDirectoryPickerService
    {
        public string? PickDirectory(string? initialDirectory, string title = IDirectoryPickerService.DefaultTitle) => result;
    }

    // --- Regressions pinned by the review -------------------------------------------------

    [StaFact]
    public void StartWorkflow_CannotExecuteWhileStartupErrorsArePresent()
    {
        // F17: the startup dialog can be dismissed, and '+' opens tabs afterwards. The gate has
        // to live on the command, not only on MainWindowViewModel.
        using var vm = Create(startupErrors: ["review_prompt.md ist leer."]);
        vm.WorkingDirectory = _root;
        vm.TaskName = "demo";

        Assert.False(vm.StartWorkflowCommand.CanExecute(null));
        Assert.Equal("review_prompt.md ist leer.", vm.ValidationMessage);
    }

    [StaFact]
    public void StartWorkflow_CanExecuteWhenThereAreNoStartupErrors()
    {
        using var vm = Create();
        vm.WorkingDirectory = _root;
        vm.TaskName = "demo";

        Assert.True(vm.StartWorkflowCommand.CanExecute(null));
    }

    [StaFact]
    public void AFailedRename_RollsTheNameBackToTheFolderOnDisk()
    {
        // Spec 8.3 / 12.2. Without the rollback, TaskName, the tab header and every path in
        // TaskPaths name a folder that does not exist, and the user cannot tell which is real.
        using var vm = Create(folders: new FailingRenameFolderService());
        vm.WorkingDirectory = _root;
        vm.TaskName = "demo";                       // created on disk

        vm.TaskName = "demo-renamed";               // rename throws

        Assert.Equal("demo", vm.TaskName);
        Assert.Equal("demo", vm.Header);
        Assert.NotNull(vm.ValidationMessage);
        Assert.True(Directory.Exists(Path.Combine(_root, "demo")));
        Assert.False(Directory.Exists(Path.Combine(_root, "demo-renamed")));
    }

    [StaTheory]
    [MemberData(nameof(StartupFailures))]
    public async Task AThrowingOrchestrator_SurfacesAMessageInsteadOfAnUnobservedTaskException(Exception failure)
    {
        // StartWorkflow does not await the run, so anything not caught in RunAsync becomes an
        // unobserved task exception while the UI silently resets IsRunning (spec 12.1).
        var unobserved = 0;
        void OnUnobserved(object? s, UnobservedTaskExceptionEventArgs e) => Interlocked.Increment(ref unobserved);
        TaskScheduler.UnobservedTaskException += OnUnobserved;

        try
        {
            using var vm = Create(orchestrator: new ThrowingOrchestrator(failure));
            vm.WorkingDirectory = _root;
            vm.TaskName = "demo";

            vm.StartWorkflowCommand.Execute(null);

            // Let the faulted task be observed and the finally block run.
            for (var i = 0; i < 50 && vm.IsRunning; i++)
            {
                await Task.Delay(20);
            }

            Assert.False(vm.IsRunning);
            Assert.NotNull(vm.ValidationMessage);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            Assert.Equal(0, Volatile.Read(ref unobserved));
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    public static TheoryData<Exception> StartupFailures() =>
    [
        new FileNotFoundException("Weder pwsh.exe noch powershell.exe wurden gefunden."),
        new PlatformNotSupportedException("Diese Windows-Version unterstuetzt keine Pseudo-Konsole."),
        new Win32Exception(2, "Der Shell-Prozess konnte nicht gestartet werden."),
        new IOException("Das Arbeitsverzeichnis wurde geloescht."),
        new UnauthorizedAccessException("Kein Zugriff."),
        new InvalidOperationException("WebView2 konnte nicht initialisiert werden."),
        new ArtifactWatchException("Das Arbeitsverzeichnis existiert nicht mehr."),
    ];


    [StaFact]
    public void Header_FallsBackToTheGermanPlaceholder()
    {
        using var vm = Create();

        Assert.Equal("(Bezeichnung)", vm.Header);

        vm.TaskName = "demo";

        Assert.Equal("demo", vm.Header);
    }

    [StaFact]
    public void Header_TreatsWhitespaceAsEmpty()
    {
        using var vm = Create();

        vm.TaskName = "   ";

        Assert.Equal("(Bezeichnung)", vm.Header);
    }

    [StaFact]
    public void Phases_AreTheFiveStationsInOrderAndStartPending()
    {
        // Arrange
        using var vm = Create();

        // Act
        var names = vm.Phases.Select(p => p.DisplayName).ToArray();

        // Assert
        Assert.Equal(
            new[] { "Spezifikation", "Vereinfachen", "Review", "Review umsetzen", "Implementierung" },
            names);
        Assert.All(vm.Phases, p => Assert.Equal(PhaseStatus.Pending, p.Status));
    }

    [StaFact]
    public void StartWorkflow_IsDisabledWithoutANameOrADirectory()
    {
        using var vm = Create();

        Assert.False(vm.StartWorkflowCommand.CanExecute(null));

        vm.WorkingDirectory = _root;
        Assert.False(vm.StartWorkflowCommand.CanExecute(null));

        vm.TaskName = "demo";
        Assert.True(vm.StartWorkflowCommand.CanExecute(null));
    }

    [StaFact]
    public void StartWorkflow_IsDisabledForAnInvalidName()
    {
        using var vm = Create();
        vm.WorkingDirectory = _root;

        vm.TaskName = "bad:name";

        Assert.False(vm.StartWorkflowCommand.CanExecute(null));
        Assert.NotNull(vm.ValidationMessage);
    }

    [StaFact]
    public void SettingAValidName_CreatesTheFolder()
    {
        using var vm = Create();
        vm.WorkingDirectory = _root;

        vm.TaskName = "demo";

        Assert.True(Directory.Exists(Path.Combine(_root, "demo")));
    }

    [StaFact]
    public void RenamingWhileIdle_RenamesTheFolder()
    {
        using var vm = Create();
        vm.WorkingDirectory = _root;
        vm.TaskName = "old";

        vm.TaskName = "new";

        Assert.False(Directory.Exists(Path.Combine(_root, "old")));
        Assert.True(Directory.Exists(Path.Combine(_root, "new")));
    }

    [StaFact]
    public void ReusingAnExistingFolder_SetsAnInfoMessage()
    {
        Directory.CreateDirectory(Path.Combine(_root, "demo"));
        using var vm = Create();
        vm.WorkingDirectory = _root;

        vm.TaskName = "demo";

        Assert.NotNull(vm.InfoMessage);
    }

    [StaFact]
    public void StartWorkflow_LocksTheNameAndMarksTheRunAsActive()
    {
        using var vm = Create();
        vm.WorkingDirectory = _root;
        vm.TaskName = "demo";

        vm.StartWorkflowCommand.Execute(null);

        Assert.True(vm.IsNameLocked);
        Assert.True(vm.IsRunning);
        Assert.False(vm.StartWorkflowCommand.CanExecute(null));
    }

    [StaFact]
    public void RenamingAfterStart_DoesNotTouchTheFolder()
    {
        using var vm = Create();
        vm.WorkingDirectory = _root;
        vm.TaskName = "demo";
        vm.StartWorkflowCommand.Execute(null);

        vm.TaskName = "renamed";

        Assert.True(Directory.Exists(Path.Combine(_root, "demo")));
        Assert.False(Directory.Exists(Path.Combine(_root, "renamed")));
    }

    [StaFact]
    public void CompleteTask_IsOnlyEnabledDuringTheImplementationPhase()
    {
        using var vm = Create();
        vm.WorkingDirectory = _root;
        vm.TaskName = "demo";
        vm.StartWorkflowCommand.Execute(null);

        Assert.False(vm.CompleteTaskCommand.CanExecute(null));

        vm.ApplyProgress(new PhaseProgress(WorkflowPhase.Implementation, PhaseStatus.Active));

        Assert.True(vm.CompleteTaskCommand.CanExecute(null));
    }

    [StaFact]
    public void StartWorkflow_RunEndsWithEveryPhaseCompleted_FinishesTheTask()
    {
        var store = new FakeTaskStateStore();
        using var vm = Create(
            orchestrator: new JournallingOrchestrator(store, PhaseStatus.Completed),
            stateStore: store);
        vm.WorkingDirectory = _root;
        vm.TaskName = "demo";

        vm.StartWorkflowCommand.Execute(null);

        Assert.False(vm.IsRunning);
        Assert.True(vm.IsTaskFinished);
        Assert.False(vm.StartWorkflowCommand.CanExecute(null));
    }

    [StaFact]
    public void StartWorkflow_RunEndsWithImplementationStillActive_DoesNotFinishTheTask()
    {
        var store = new FakeTaskStateStore();
        using var vm = Create(
            orchestrator: new JournallingOrchestrator(store, PhaseStatus.Active),
            stateStore: store);
        vm.WorkingDirectory = _root;
        vm.TaskName = "demo";

        vm.StartWorkflowCommand.Execute(null);

        Assert.False(vm.IsRunning);
        Assert.False(vm.IsTaskFinished);
        Assert.True(vm.StartWorkflowCommand.CanExecute(null));
    }

    // --- Queue (WF_QUEUE spec section 4) ---

    [StaFact]
    public void StartWorkflow_RunEndsWithEveryPhaseCompleted_RaisesFinishedOnceWithTheRunSettled()
    {
        // Arrange
        var store = new FakeTaskStateStore();
        using var vm = Create(orchestrator: new JournallingOrchestrator(store, PhaseStatus.Completed), stateStore: store);
        vm.WorkingDirectory = _root;
        vm.TaskName = "demo";
        var raised = 0;
        bool? runningAtRaise = null;
        vm.Finished += (_, _) =>
        {
            raised++;
            runningAtRaise ??= vm.IsRunning;
        };

        // Act
        vm.StartWorkflowCommand.Execute(null);

        // Assert
        Assert.Equal(1, raised);
        Assert.False(runningAtRaise);
    }

    [StaFact]
    public void StartWorkflow_RunEndsWithImplementationStillActive_RaisesNoFinished()
    {
        // Arrange
        var store = new FakeTaskStateStore();
        using var vm = Create(orchestrator: new JournallingOrchestrator(store, PhaseStatus.Active), stateStore: store);
        vm.WorkingDirectory = _root;
        vm.TaskName = "demo";
        var raised = 0;
        vm.Finished += (_, _) => raised++;

        // Act
        vm.StartWorkflowCommand.Execute(null);

        // Assert
        Assert.Equal(0, raised);
    }

    [StaFact]
    public void StartWorkflow_ClearsTheQueue()
    {
        // Arrange
        using var predecessor = Create();
        using var vm = Create();
        vm.WorkingDirectory = _root;
        vm.TaskName = "demo";
        vm.QueuedAfter = predecessor;

        // Act
        vm.StartWorkflowCommand.Execute(null);

        // Assert
        Assert.True(vm.IsRunning);
        Assert.Null(vm.QueuedAfter);
    }

    [StaFact]
    public void StartWorkflow_RefusedByTheStartGate_KeepsTheQueue()
    {
        // Arrange: no task name, so the start gate refuses.
        using var predecessor = Create();
        using var vm = Create();
        vm.WorkingDirectory = _root;
        vm.QueuedAfter = predecessor;

        // Act
        vm.StartWorkflowCommand.Execute(null);

        // Assert
        Assert.False(vm.IsRunning);
        Assert.Same(predecessor, vm.QueuedAfter);
    }

    [StaFact]
    public void StartWorkflow_DescriptionCannotBeSaved_StartsNothingKeepsTheQueueAndSaysSo()
    {
        // Arrange
        var store = new FakeTaskStateStore { FailDescriptionWriteFor = "demo" };
        using var predecessor = Create();
        using var vm = Create(stateStore: store);
        vm.WorkingDirectory = _root;
        vm.TaskName = "demo";
        vm.QueuedAfter = predecessor;

        // Act
        var thrown = Record.Exception(() => vm.StartWorkflowCommand.Execute(null));

        // Assert
        Assert.Null(thrown);
        Assert.False(vm.IsRunning);
        Assert.False(vm.IsNameLocked);
        Assert.Same(predecessor, vm.QueuedAfter);
        Assert.Equal("Die Taskbeschreibung konnte nicht gespeichert werden: simulated description write failure", vm.ValidationMessage);
    }

    [StaFact]
    public void StartIfQueuedAfter_WhenTheStartGateIsShut_StartsNothingAndSaysSo()
    {
        // Arrange
        using var predecessor = Create();
        predecessor.TaskName = "alpha";
        using var vm = Create();
        vm.WorkingDirectory = _root;
        vm.QueuedAfter = predecessor;

        // Act
        var started = vm.StartIfQueuedAfter(predecessor);

        // Assert
        Assert.False(started);
        Assert.False(vm.IsRunning);
        Assert.Null(vm.QueuedAfter);
        Assert.Equal("Queue: 'alpha' ist fertig, der Workflow konnte aber nicht automatisch gestartet werden.", vm.InfoMessage);
    }

    [StaFact]
    public void CancelIfQueuedAfter_ClearsOnlyAQueueOnThatTabAndSaysSo()
    {
        // Arrange
        using var predecessor = Create();
        predecessor.TaskName = "alpha";
        using var other = Create();
        using var vm = Create();
        vm.QueuedAfter = predecessor;

        // Act
        vm.CancelIfQueuedAfter(other);
        var afterOther = vm.QueuedAfter;
        vm.CancelIfQueuedAfter(predecessor);

        // Assert
        Assert.Same(predecessor, afterOther);
        Assert.Null(vm.QueuedAfter);
        Assert.Equal("Queue aufgehoben: 'alpha' wurde geschlossen.", vm.InfoMessage);
    }

    [StaFact]
    public void IsQueueEditable_IsFalseWhileTheTabRuns()
    {
        // Arrange
        using var vm = Create();
        vm.WorkingDirectory = _root;
        vm.TaskName = "demo";
        var before = vm.IsQueueEditable;
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // Act
        vm.StartWorkflowCommand.Execute(null);

        // Assert
        Assert.True(before);
        Assert.False(vm.IsQueueEditable);
        Assert.Contains(nameof(TaskTabViewModel.IsQueueEditable), raised);
    }

    [StaFact]
    public void ApplyProgress_DrivesTheIndicatorColours()
    {
        using var vm = Create();

        vm.ApplyProgress(new PhaseProgress(WorkflowPhase.Specification, PhaseStatus.Active));
        Assert.Equal(PhaseStatus.Active, vm.Phases[(int)WorkflowPhase.Specification].Status);

        vm.ApplyProgress(new PhaseProgress(WorkflowPhase.Specification, PhaseStatus.Completed));
        vm.ApplyProgress(new PhaseProgress(WorkflowPhase.Simplifier, PhaseStatus.Active));

        Assert.Equal(PhaseStatus.Completed, vm.Phases[(int)WorkflowPhase.Specification].Status);
        Assert.Equal(PhaseStatus.Active, vm.Phases[(int)WorkflowPhase.Simplifier].Status);
        Assert.Equal(PhaseStatus.Pending, vm.Phases[(int)WorkflowPhase.Review].Status);
    }

    [StaFact]
    public void SelectingADirectory_AddsItToTheSharedRecentList()
    {
        using var vm = Create();

        vm.WorkingDirectory = _root;

        Assert.Contains(_root, _settings.Settings.RecentDirectories);
        Assert.Contains(_root, vm.RecentDirectories);
    }

    [StaFact]
    public void CloseCommand_RaisesCloseRequested()
    {
        using var vm = Create();
        var raised = false;
        vm.CloseRequested += (_, _) => raised = true;

        vm.CloseCommand.Execute(null);

        Assert.True(raised);
    }

    [Fact]
    public void LoadForResume_PrefillsEveryFieldAndFlipsTheButton()
    {
        var store = new FakeTaskStateStore();
        using var vm = Create(stateStore: store);

        var paths = new TaskPaths(_root, "alpha");
        Directory.CreateDirectory(paths.TaskDirectory);

        var state = new TaskState { TaskDescription = "die Beschreibung", UpdatedUtc = DateTimeOffset.UtcNow };
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.Specification, PhaseStatus.Completed, DateTimeOffset.UtcNow));
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.Simplifier, PhaseStatus.Completed, DateTimeOffset.UtcNow));
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.Review, PhaseStatus.Active, null));
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.ResolveReview, PhaseStatus.Pending, null));
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.Implementation, PhaseStatus.Pending, null));

        vm.LoadForResume(new RecoverableTask(paths, state, WorkflowPhase.Review));

        Assert.Equal("alpha", vm.TaskName);
        Assert.Equal("die Beschreibung", vm.TaskDescription);
        Assert.Equal(_root, vm.WorkingDirectory);
        Assert.True(vm.IsNameLocked);
        Assert.True(vm.IsRecovered);
        Assert.Equal(WorkflowPhase.Review, vm.ResumePhase);
        Assert.Equal("Continue workflow", vm.StartButtonLabel);
        Assert.Equal(PhaseStatus.Completed, vm.Phases[(int)WorkflowPhase.Specification].Status);
        Assert.Equal(PhaseStatus.Completed, vm.Phases[(int)WorkflowPhase.Simplifier].Status);

        // An Active in the journal means the app died mid-phase: it is about to be re-run.
        Assert.Equal(PhaseStatus.Pending, vm.Phases[(int)WorkflowPhase.Review].Status);
    }

    [Fact]
    public void StartButtonLabel_FreshTab_SaysStartWorkflow()
    {
        using var vm = Create();

        Assert.Equal("Start workflow", vm.StartButtonLabel);
        Assert.Null(vm.ResumePhase);
        Assert.False(vm.IsRecovered);
    }

    [Fact]
    public void StartWorkflow_OnARecoveredTab_RunsFromTheResumePhase()
    {
        var store = new FakeTaskStateStore();
        var orchestrator = new CapturingOrchestrator();
        using var vm = Create(orchestrator: orchestrator, stateStore: store);

        var paths = new TaskPaths(_root, "alpha");
        Directory.CreateDirectory(paths.TaskDirectory);
        var state = new TaskState { TaskDescription = "d", UpdatedUtc = DateTimeOffset.UtcNow };
        foreach (var definition in PhaseCatalog.All)
        {
            state.Phases.Add(new TaskPhaseState(definition.Phase, PhaseStatus.Pending, null));
        }

        vm.LoadForResume(new RecoverableTask(paths, state, WorkflowPhase.ResolveReview));
        vm.StartWorkflowCommand.Execute(null);

        Assert.NotNull(orchestrator.Request);
        Assert.Equal(WorkflowPhase.ResolveReview, orchestrator.Request.StartPhase);
        Assert.Contains("d", store.Descriptions);
    }

    [Fact]
    public void NotifyClosedByUser_RecoveredTab_Dismisses()
    {
        var store = new FakeTaskStateStore();
        using var vm = Create(stateStore: store);

        var paths = new TaskPaths(_root, "alpha");
        Directory.CreateDirectory(paths.TaskDirectory);
        var state = new TaskState { UpdatedUtc = DateTimeOffset.UtcNow };
        foreach (var definition in PhaseCatalog.All)
        {
            state.Phases.Add(new TaskPhaseState(definition.Phase, PhaseStatus.Pending, null));
        }

        vm.LoadForResume(new RecoverableTask(paths, state, WorkflowPhase.Specification));
        vm.NotifyClosedByUser();

        Assert.Contains(store.Dismissals, d => d.Dismissed);
    }

    [Fact]
    public void NotifyClosedByUser_FreshTab_DoesNothing()
    {
        var store = new FakeTaskStateStore();
        using var vm = Create(stateStore: store);
        vm.WorkingDirectory = _root;
        vm.TaskName = "alpha";

        vm.NotifyClosedByUser();

        Assert.Empty(store.Dismissals);
    }

    [Fact]
    public void TypingAnUnfinishedTaskName_ReArmsContinue()
    {
        var store = new FakeTaskStateStore();
        var paths = new TaskPaths(_root, "alpha");
        Directory.CreateDirectory(paths.TaskDirectory);

        // The journal's "phase 1 Completed" must be backed by the artefacts, or the shared
        // demote-never-promote reconciliation correctly pushes the resume point back to phase 1
        // (SPEC 6.3.2). This test is about the RE-ARM, not about demotion.
        File.WriteAllText(paths.SpecAbsolute, "spec");
        File.WriteAllText(paths.PlanAbsolute, "plan");

        var state = new TaskState { TaskDescription = "d", UpdatedUtc = DateTimeOffset.UtcNow };
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.Specification, PhaseStatus.Completed, DateTimeOffset.UtcNow));
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.Simplifier, PhaseStatus.Pending, null));
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.Review, PhaseStatus.Pending, null));
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.ResolveReview, PhaseStatus.Pending, null));
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.Implementation, PhaseStatus.Pending, null));
        store.Seed(paths, state);

        using var vm = Create(stateStore: store);
        vm.WorkingDirectory = _root;
        vm.TaskName = "alpha";

        Assert.Equal(WorkflowPhase.Simplifier, vm.ResumePhase);
        Assert.Equal("Continue workflow", vm.StartButtonLabel);
    }

    /// <summary>
    /// The re-arm path reconciles through the same helper as the startup scan, so a journal that
    /// stopped at "phase 1 Active" while spec and plan were already written resumes at the
    /// simplifier and paints phase 1 green, instead of re-running the specification.
    /// </summary>
    [Fact]
    public void TypingATaskWhoseSpecificationIsActiveButOnDisk_OffersContinueAtSimplifierWithSpecificationGreen()
    {
        // Arrange
        var store = new FakeTaskStateStore();
        var paths = new TaskPaths(_root, "alpha");
        Directory.CreateDirectory(paths.TaskDirectory);
        File.WriteAllText(paths.SpecAbsolute, "spec");
        File.WriteAllText(paths.PlanAbsolute, "plan");

        var state = new TaskState { TaskDescription = "d", UpdatedUtc = new DateTimeOffset(2026, 10, 1, 15, 0, 0, TimeSpan.Zero) };
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.Specification, PhaseStatus.Active, null));
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.Simplifier, PhaseStatus.Pending, null));
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.Review, PhaseStatus.Pending, null));
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.ResolveReview, PhaseStatus.Pending, null));
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.Implementation, PhaseStatus.Pending, null));
        store.Seed(paths, state);

        using var vm = Create(stateStore: store);

        // Act
        vm.WorkingDirectory = _root;
        vm.TaskName = "alpha";

        // Assert
        Assert.Equal(WorkflowPhase.Simplifier, vm.ResumePhase);
        Assert.Equal("Continue workflow", vm.StartButtonLabel);
        Assert.Equal(PhaseStatus.Completed, vm.Phases[(int)WorkflowPhase.Specification].Status);
        Assert.Equal(PhaseStatus.Pending, vm.Phases[(int)WorkflowPhase.Simplifier].Status);
        Assert.NotEmpty(store.Replacements);
    }

    /// <summary>
    /// Promotion needs a journal: a folder that merely holds artefacts was never run by this
    /// application, and reusing it for a new Taskbeschreibung must not skip the specification
    /// (resume design section 6.3, BASE section 8.3).
    /// </summary>
    [Fact]
    public void TypingATaskFolderWithArtefactsButNoJournal_StaysAFreshStart()
    {
        // Arrange
        var store = new FakeTaskStateStore();
        var paths = new TaskPaths(_root, "alpha");
        Directory.CreateDirectory(paths.TaskDirectory);
        File.WriteAllText(paths.SpecAbsolute, "spec");
        File.WriteAllText(paths.PlanAbsolute, "plan");
        File.WriteAllText(paths.ReviewAbsolute, "review");

        using var vm = Create(stateStore: store);

        // Act
        vm.WorkingDirectory = _root;
        vm.TaskName = "alpha";

        // Assert
        Assert.Null(vm.ResumePhase);
        Assert.Equal("Start workflow", vm.StartButtonLabel);
        Assert.All(vm.Phases, p => Assert.Equal(PhaseStatus.Pending, p.Status));
        Assert.Empty(store.Replacements);
    }

    [Fact]
    public void SwitchingToADirectoryWithoutAJournal_ClearsResumeAndRepaintsEveryIndicatorGrey()
    {
        var store = new FakeTaskStateStore();

        var other = Path.Combine(_root, "other-workspace");
        Directory.CreateDirectory(other);

        var alpha = new TaskPaths(_root, "alpha");
        Directory.CreateDirectory(alpha.TaskDirectory);

        // Backs the journal's "phase 1 Completed" so the reconciliation leaves it alone; this
        // test is about the RESET on switching directory, not about demotion.
        File.WriteAllText(alpha.SpecAbsolute, "spec");
        File.WriteAllText(alpha.PlanAbsolute, "plan");

        var state = new TaskState { TaskDescription = "d", UpdatedUtc = DateTimeOffset.UtcNow };
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.Specification, PhaseStatus.Completed, DateTimeOffset.UtcNow));
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.Simplifier, PhaseStatus.Pending, null));
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.Review, PhaseStatus.Pending, null));
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.ResolveReview, PhaseStatus.Pending, null));
        state.Phases.Add(new TaskPhaseState(WorkflowPhase.Implementation, PhaseStatus.Pending, null));
        store.Seed(alpha, state);

        using var vm = Create(stateStore: store);
        vm.WorkingDirectory = _root;
        vm.TaskName = "alpha";
        Assert.Equal(PhaseStatus.Completed, vm.Phases[(int)WorkflowPhase.Specification].Status);
        Assert.Equal(WorkflowPhase.Simplifier, vm.ResumePhase);

        // Same task NAME, different working directory - the case that actually reaches a
        // different folder. (Retyping the name alone renames the folder and carries its journal
        // along with it, so the resume state legitimately survives that.) The green indicator
        // belonged to the old directory's alpha and would otherwise claim that this alpha's
        // phase 1 is finished (SPEC 6.6, R15).
        vm.WorkingDirectory = other;

        Assert.Null(vm.ResumePhase);
        Assert.Equal("Start workflow", vm.StartButtonLabel);
        Assert.All(vm.Phases, p => Assert.Equal(PhaseStatus.Pending, p.Status));
    }

    [Fact]
    public void TypingACompletedTaskName_ClearsResumeAndRepaintsEveryIndicatorGrey()
    {
        var store = new FakeTaskStateStore();
        var paths = new TaskPaths(_root, "fertig");
        Directory.CreateDirectory(paths.TaskDirectory);

        // A genuinely finished task has every artefact on disk; without them the reconciliation
        // would rightly demote phase 1 and this would no longer be the "completed task" case.
        File.WriteAllText(paths.SpecAbsolute, "spec");
        File.WriteAllText(paths.PlanAbsolute, "plan");
        File.WriteAllText(paths.SimplifiedAbsolute, "simplified");
        File.WriteAllText(paths.ReviewAbsolute, "review");
        File.WriteAllText(paths.DoneAbsolute, "done");

        var state = new TaskState { TaskDescription = "d", UpdatedUtc = DateTimeOffset.UtcNow };
        foreach (var definition in PhaseCatalog.All)
        {
            state.Phases.Add(new TaskPhaseState(definition.Phase, PhaseStatus.Completed, DateTimeOffset.UtcNow));
        }

        store.Seed(paths, state);

        using var vm = Create(stateStore: store);
        vm.WorkingDirectory = _root;
        vm.TaskName = "fertig";

        // The button already reads 'Start workflow' because nothing is resumable. Five green
        // indicators above it would paint an about-to-start run as already finished.
        Assert.Null(vm.ResumePhase);
        Assert.Equal("Start workflow", vm.StartButtonLabel);
        Assert.All(vm.Phases, p => Assert.Equal(PhaseStatus.Pending, p.Status));
    }

    [Fact]
    public void TypingATaskWhoseArtefactVanished_ReconcilesLikeTheStartupScan()
    {
        var store = new TaskStateStore();
        var paths = new TaskPaths(_root, "alpha");
        Directory.CreateDirectory(paths.TaskDirectory);

        store.SaveDescription(paths, "d");
        File.WriteAllText(paths.SpecAbsolute, "spec");
        File.WriteAllText(paths.PlanAbsolute, "plan");
        store.RecordPhase(paths, WorkflowPhase.Specification, PhaseStatus.Completed);

        // The plan is gone, so phase 1 is no longer backed by its artefacts.
        File.Delete(paths.PlanAbsolute);

        using var vm = Create(stateStore: store);
        vm.WorkingDirectory = _root;
        vm.TaskName = "alpha";

        // Without the shared reconciliation this reads Review and resumes phase 2 with no plan
        // on disk (SPEC 6.3.2, R14).
        Assert.Equal(WorkflowPhase.Specification, vm.ResumePhase);
        Assert.All(vm.Phases, p => Assert.Equal(PhaseStatus.Pending, p.Status));
    }

    [Fact]
    public void StartWorkflow_OnARecoveredTab_DoesNotRecomputeResumePhase()
    {
        var store = new FakeTaskStateStore();
        var orchestrator = new CapturingOrchestrator();
        using var vm = Create(orchestrator: orchestrator, stateStore: store);

        var paths = new TaskPaths(_root, "alpha");
        Directory.CreateDirectory(paths.TaskDirectory);

        // The journal on disk still claims phases 1-4 are complete; the recovered tab was handed
        // the reconciled view that demoted them. StartWorkflow calls SyncFolder directly, and the
        // re-arm must not overwrite ResumePhase from that journal (SPEC 6.6).
        var onDisk = new TaskState { TaskDescription = "d", UpdatedUtc = DateTimeOffset.UtcNow };
        onDisk.Phases.Add(new TaskPhaseState(WorkflowPhase.Specification, PhaseStatus.Completed, DateTimeOffset.UtcNow));
        onDisk.Phases.Add(new TaskPhaseState(WorkflowPhase.Simplifier, PhaseStatus.Completed, DateTimeOffset.UtcNow));
        onDisk.Phases.Add(new TaskPhaseState(WorkflowPhase.Review, PhaseStatus.Completed, DateTimeOffset.UtcNow));
        onDisk.Phases.Add(new TaskPhaseState(WorkflowPhase.ResolveReview, PhaseStatus.Completed, DateTimeOffset.UtcNow));
        onDisk.Phases.Add(new TaskPhaseState(WorkflowPhase.Implementation, PhaseStatus.Pending, null));
        store.Seed(paths, onDisk);

        var reconciled = new TaskState { TaskDescription = "d", UpdatedUtc = DateTimeOffset.UtcNow };
        foreach (var definition in PhaseCatalog.All)
        {
            reconciled.Phases.Add(new TaskPhaseState(definition.Phase, PhaseStatus.Pending, null));
        }

        vm.LoadForResume(new RecoverableTask(paths, reconciled, WorkflowPhase.Specification));
        vm.StartWorkflowCommand.Execute(null);

        Assert.NotNull(orchestrator.Request);
        Assert.Equal(WorkflowPhase.Specification, orchestrator.Request.StartPhase);
    }

    [StaFact]
    public void ApplyLauncherStatus_StallReported_SetsTheLauncherStatusMessage()
    {
        // Arrange
        using var vm = Create();

        // Act
        vm.ApplyLauncherStatus("Der Launcher laeuft nicht.");

        // Assert
        Assert.Equal("Der Launcher laeuft nicht.", vm.LauncherStatusMessage);
    }

    [StaFact]
    public void ApplyLauncherStatus_Cleared_LeavesAnUnrelatedInfoMessageIntact()
    {
        // Arrange - the tab is already telling the user something independent of any launcher.
        using var vm = Create();
        vm.InfoMessage = "Ordner existiert bereits und wird weiterverwendet.";

        // Act
        vm.ApplyLauncherStatus("Der Launcher laeuft nicht.");
        vm.ApplyLauncherStatus(null);

        // Assert - clearing the stall must not erase the folder notice that was there before it.
        Assert.Null(vm.LauncherStatusMessage);
        Assert.Equal("Ordner existiert bereits und wird weiterverwendet.", vm.InfoMessage);
    }

    [StaFact]
    public void StartWorkflow_PassesALauncherStatusSinkToTheOrchestrator()
    {
        // Arrange - without this wiring the orchestrator's refusal to paste would be reported to
        // nothing, and a paused session would look like a terminal that stopped for no reason.
        var orchestrator = new CapturingOrchestrator();
        using var vm = Create(orchestrator: orchestrator);
        vm.WorkingDirectory = _root;
        vm.TaskName = "LauncherSink";
        vm.TaskDescription = "d";

        // Act
        vm.StartWorkflowCommand.Execute(null);

        // Assert
        Assert.NotNull(orchestrator.Request);
        Assert.NotNull(orchestrator.Request.LauncherStatus);
    }

    [StaFact]
    public async Task RunAsync_RunEndsWithAnException_ClearsTheLauncherStatus()
    {
        // Arrange - the orchestrator clears its own status on every ordinary exit, but a run that
        // unwinds through an exception never reaches that point.
        using var vm = Create(orchestrator: new ThrowingOrchestrator(new IOException("boom")));
        vm.WorkingDirectory = _root;
        vm.TaskName = "LauncherStallCleared";
        vm.TaskDescription = "d";
        vm.ApplyLauncherStatus("Der Launcher laeuft nicht.");

        // Act
        vm.StartWorkflowCommand.Execute(null);

        for (var i = 0; i < 50 && vm.IsRunning; i++)
        {
            await Task.Delay(20);
        }

        // Assert - a stall reason must not outlive the run that produced it.
        Assert.False(vm.IsRunning);
        Assert.Null(vm.LauncherStatusMessage);
    }

    // --- A task opened with 'Task laden' (Workflow_LOAD_AND_PAUSE spec section 5.5) ------------

    private static readonly DateTimeOffset LoadedPhaseDone = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private static TaskState LoadedJournal(params PhaseStatus[] statuses)
    {
        var state = new TaskState { TaskDescription = "d", UpdatedUtc = LoadedPhaseDone };

        for (var i = 0; i < PhaseCatalog.All.Count; i++)
        {
            state.Phases.Add(new TaskPhaseState(
                PhaseCatalog.All[i].Phase,
                statuses[i],
                statuses[i] == PhaseStatus.Completed ? LoadedPhaseDone : null));
        }

        return state;
    }

    [Fact]
    public void LoadForResume_FinishedTask_KeepsFiveGreenIndicatorsAndOffersAFreshStart()
    {
        // Arrange
        using var vm = Create();
        var paths = new TaskPaths(_root, "alpha");
        Directory.CreateDirectory(paths.TaskDirectory);
        var state = LoadedJournal(
            PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Completed);

        // Act
        vm.LoadForResume(new RecoverableTask(paths, state, ResumePhase: null));

        // Assert
        Assert.All(vm.Phases, p => Assert.Equal(PhaseStatus.Completed, p.Status));
        Assert.Null(vm.ResumePhase);
        Assert.Equal("Start workflow", vm.StartButtonLabel);
        Assert.Equal("Alle Phasen sind abgeschlossen. 'Start workflow' beginnt den Task von vorn.", vm.InfoMessage);
        Assert.True(vm.IsNameLocked);
        Assert.True(vm.StartWorkflowCommand.CanExecute(null));
    }

    [Fact]
    public void LoadForResume_UnfinishedTask_WritesNoInfoLine()
    {
        // Arrange
        using var vm = Create();
        var paths = new TaskPaths(_root, "alpha");
        Directory.CreateDirectory(paths.TaskDirectory);
        var state = LoadedJournal(
            PhaseStatus.Completed, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending);

        // Act
        vm.LoadForResume(new RecoverableTask(paths, state, WorkflowPhase.Simplifier));

        // Assert
        Assert.Null(vm.InfoMessage);
    }

    [Fact]
    public void LoadForResume_CreatedJournal_SaysSoAndNamesTheNextPhase()
    {
        // Arrange - Workflow_LoadFix spec 5.4.
        using var vm = Create();
        var paths = new TaskPaths(_root, "alpha");
        Directory.CreateDirectory(paths.TaskDirectory);
        var state = LoadedJournal(
            PhaseStatus.Completed, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending);

        // Act
        vm.LoadForResume(new RecoverableTask(paths, state, WorkflowPhase.Simplifier, JournalCreated: true));

        // Assert
        Assert.Equal("Neue .workflow-state.json angelegt. Nächste Phase: Vereinfachen.", vm.InfoMessage);
        Assert.Equal("Continue workflow", vm.StartButtonLabel);
    }

    [Fact]
    public void LoadForResume_SkippedReviewPhases_ShowsThemSkipped()
    {
        // Arrange
        using var vm = Create();
        var paths = new TaskPaths(_root, "alpha");
        Directory.CreateDirectory(paths.TaskDirectory);
        var state = LoadedJournal(
            PhaseStatus.Completed, PhaseStatus.Skipped, PhaseStatus.Skipped, PhaseStatus.Skipped, PhaseStatus.Pending);

        // Act
        vm.LoadForResume(new RecoverableTask(paths, state, WorkflowPhase.Implementation));

        // Assert
        Assert.Equal(
            new[] { PhaseStatus.Completed, PhaseStatus.Skipped, PhaseStatus.Skipped, PhaseStatus.Skipped, PhaseStatus.Pending },
            vm.Phases.Select(p => p.Status));
        Assert.Null(vm.InfoMessage);
    }

    [Fact]
    public void StartWorkflow_RestartingAFinishedTask_TurnsEveryIndicatorGrey()
    {
        // Arrange
        var orchestrator = new CapturingOrchestrator();
        using var vm = Create(orchestrator: orchestrator);
        var paths = new TaskPaths(_root, "alpha");
        Directory.CreateDirectory(paths.TaskDirectory);
        vm.LoadForResume(new RecoverableTask(
            paths,
            LoadedJournal(PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Completed),
            ResumePhase: null));

        // Act
        vm.StartWorkflowCommand.Execute(null);

        // Assert - the orchestrator clears the journal from phase 1 on; the indicators follow suit.
        Assert.NotNull(orchestrator.Request);
        Assert.Equal(WorkflowPhase.Specification, orchestrator.Request.StartPhase);
        Assert.All(vm.Phases, p => Assert.Equal(PhaseStatus.Pending, p.Status));
    }

    [Fact]
    public void StartWorkflow_ResetsTheIndicatorsFromTheStartPhaseOnAndKeepsEarlierOnes()
    {
        // Arrange - an irregular journal: Review umsetzen recorded Completed after an incomplete Review.
        var orchestrator = new CapturingOrchestrator();
        using var vm = Create(orchestrator: orchestrator);
        var paths = new TaskPaths(_root, "alpha");
        Directory.CreateDirectory(paths.TaskDirectory);
        vm.LoadForResume(new RecoverableTask(
            paths,
            LoadedJournal(PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Pending, PhaseStatus.Completed, PhaseStatus.Pending),
            WorkflowPhase.Review));
        Assert.Equal(PhaseStatus.Completed, vm.Phases[(int)WorkflowPhase.ResolveReview].Status);   // positive control

        // Act
        vm.StartWorkflowCommand.Execute(null);

        // Assert
        Assert.NotNull(orchestrator.Request);
        Assert.Equal(WorkflowPhase.Review, orchestrator.Request.StartPhase);
        Assert.Equal(PhaseStatus.Completed, vm.Phases[(int)WorkflowPhase.Specification].Status);
        Assert.Equal(PhaseStatus.Completed, vm.Phases[(int)WorkflowPhase.Simplifier].Status);
        Assert.Equal(PhaseStatus.Pending, vm.Phases[(int)WorkflowPhase.Review].Status);
        Assert.Equal(PhaseStatus.Pending, vm.Phases[(int)WorkflowPhase.ResolveReview].Status);
        Assert.Equal(PhaseStatus.Pending, vm.Phases[(int)WorkflowPhase.Implementation].Status);
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
}
