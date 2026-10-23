using System.IO;
using System.Security.Cryptography;
using System.Windows.Threading;
using Workflow.Models;
using Workflow.Services;
using Workflow.Terminal;
using Workflow.Tests.Fakes;
using Workflow.ViewModels;

namespace Workflow.Tests;

public sealed class MainWindowViewModelTests : IDisposable
{
    private readonly string _root;
    private readonly SettingsService _settings;

    public MainWindowViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wf-main-" + Guid.NewGuid().ToString("N"));
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

    private sealed class StubFactory(
        SettingsService settings,
        IReadOnlyList<string> startupErrors,
        IWorkflowOrchestrator? orchestrator = null,
        ITaskStateStore? stateStore = null)
        : ITaskTabViewModelFactory
    {
        // CA2000 cannot trace either disposal: WebViewEnvironmentProvider is a deliberate
        // app-wide singleton never disposed per-tab in production either, and TerminalViewModel's
        // ownership passes into the returned TaskTabViewModel, whose own Dispose() disposes it.
#pragma warning disable CA2000
        public TaskTabViewModel Create() => new(
            new TaskFolderService(),
            orchestrator ?? new StubOrchestrator(),
            settings,
            stateStore ?? new FakeTaskStateStore(),
            new StubPicker(),
            new TerminalViewModel(
                new WebViewEnvironmentProvider(),
                new ConPtySessionFactory(),
                Dispatcher.CurrentDispatcher),
            TimeSpan.Zero,
            startupErrors);
#pragma warning restore CA2000

        private sealed class StubOrchestrator : IWorkflowOrchestrator
        {
            public Task RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken) =>
                Task.Delay(Timeout.Infinite, cancellationToken);
        }

        private sealed class StubPicker : IDirectoryPickerService
        {
            public string? PickDirectory(string? initialDirectory, string title = IDirectoryPickerService.DefaultTitle) => null;
        }
    }

    private MainWindowViewModel Create(
        IReadOnlyList<string>? startupErrors = null,
        StubScanner? scanner = null,
        ScriptedPicker? picker = null,
        RecordingMessages? messages = null,
        IWorkflowOrchestrator? orchestrator = null,
        ScriptedFilePicker? filePicker = null,
        ITaskFolderService? folders = null,
        ITaskStateStore? stateStore = null) =>
        new(
            new StubFactory(_settings, startupErrors ?? [], orchestrator, stateStore),
            scanner ?? new StubScanner(),
            picker ?? new ScriptedPicker(),
            filePicker ?? new ScriptedFilePicker(),
            folders ?? new TaskFolderService(),
            messages ?? new RecordingMessages(),
            _settings,
            startupErrors ?? []);

    /// <summary>
    /// The shell after one 'Neuer Task': the blank, selected tab every test written before WF_TABZUVIEL
    /// found at startup.
    /// </summary>
    private MainWindowViewModel CreateWithNewTab(
        IReadOnlyList<string>? startupErrors = null,
        StubScanner? scanner = null,
        ScriptedPicker? picker = null,
        RecordingMessages? messages = null,
        IWorkflowOrchestrator? orchestrator = null,
        ScriptedFilePicker? filePicker = null,
        ITaskFolderService? folders = null)
    {
        var vm = Create(startupErrors, scanner, picker, messages, orchestrator, filePicker, folders);
        vm.AddTaskTabCommand.Execute(null);
        return vm;
    }

    /// <summary>
    /// Delegates to the real <see cref="TaskFolderService"/>, but lets a given AdoptArtefact call
    /// (1-based) throw instead; records every AdoptArtefact call, including the ones that throw.
    /// </summary>
    private sealed class ScriptedFolderService : ITaskFolderService
    {
        private readonly TaskFolderService _inner = new();

        public Dictionary<int, Exception> Failures { get; } = [];

        public List<(string Source, string Target)> AdoptCalls { get; } = [];

        public TaskNameValidation Validate(string? taskName, string? workingDirectory) =>
            _inner.Validate(taskName, workingDirectory);

        public void EnsureCreated(TaskPaths paths) => _inner.EnsureCreated(paths);

        public bool DirectoryAlreadyExisted(TaskPaths paths) => _inner.DirectoryAlreadyExisted(paths);

        public void Rename(string workingDirectory, string oldName, string newName) =>
            _inner.Rename(workingDirectory, oldName, newName);

        public IReadOnlyList<string> FindMissingPlanningArtefacts(TaskPaths paths) =>
            _inner.FindMissingPlanningArtefacts(paths);

        public void AdoptArtefact(string sourceFile, string artefactPath)
        {
            AdoptCalls.Add((sourceFile, artefactPath));
            if (Failures.TryGetValue(AdoptCalls.Count, out var failure))
            {
                throw failure;
            }

            _inner.AdoptArtefact(sourceFile, artefactPath);
        }
    }

    /// <summary>Returns the queued files in order, then null (cancelled); records every call.</summary>
    private sealed class ScriptedFilePicker(params string?[] results) : IFilePickerService
    {
        private readonly Queue<string?> _results = new(results);

        public List<(string? InitialDirectory, string Title)> Calls { get; } = [];

        public string? PickMarkdownFile(string? initialDirectory, string title)
        {
            Calls.Add((initialDirectory, title));
            return _results.Count > 0 ? _results.Dequeue() : null;
        }
    }

    /// <summary>Returns the queued folders in order, then null (cancelled); records every call.</summary>
    private sealed class ScriptedPicker(params string?[] results) : IDirectoryPickerService
    {
        private readonly Queue<string?> _results = new(results);

        public List<(string? InitialDirectory, string Title)> Calls { get; } = [];

        public string? PickDirectory(string? initialDirectory, string title = IDirectoryPickerService.DefaultTitle)
        {
            Calls.Add((initialDirectory, title));
            return _results.Count > 0 ? _results.Dequeue() : null;
        }
    }

    private sealed class RecordingMessages(params bool[] answers) : IMessageDialogService
    {
        private readonly Queue<bool> _answers = new(answers);

        public List<string> Errors { get; } = [];

        public List<string> Questions { get; } = [];

        public void ShowError(string message) => Errors.Add(message);

        /// <summary>Returns the queued answers in order, then false ("Nein").</summary>
        public bool Confirm(string message)
        {
            Questions.Add(message);
            return _answers.Count > 0 && _answers.Dequeue();
        }
    }

    // --- Regressions pinned by the review -------------------------------------------------

    [StaFact]
    public void StartupErrors_DisableStartOnTheInitialTab()
    {
        var vm = CreateWithNewTab(["review_prompt.md ist leer."]);

        Assert.True(vm.HasStartupErrors);
        Assert.False(vm.Tabs[0].StartWorkflowCommand.CanExecute(null));
    }

    [StaFact]
    public void StartupErrors_DisableStartOnTabsOpenedAfterTheDialogWasDismissed()
    {
        // F17: the modal is dismissible and '+' keeps working, so the gate must travel with the
        // factory rather than living only on this view model.
        var vm = CreateWithNewTab(["review_prompt.md ist leer."]);

        vm.AddTaskTabCommand.Execute(null);

        Assert.Equal(2, vm.Tabs.Count);
        Assert.All(vm.Tabs, tab => Assert.False(tab.StartWorkflowCommand.CanExecute(null)));
    }

    [StaFact]
    public void Startup_OpensNoTab()
    {
        // Arrange / Act
        var vm = Create();

        // Assert
        Assert.Empty(vm.Tabs);
        Assert.Null(vm.SelectedTab);
    }

    [StaFact]
    public void AddTaskTab_AppendsAndSelectsTheNewTab()
    {
        var vm = CreateWithNewTab();

        vm.AddTaskTabCommand.Execute(null);

        Assert.Equal(2, vm.Tabs.Count);
        Assert.Same(vm.Tabs[1], vm.SelectedTab);
    }

    [StaFact]
    public void SelectingATab_MarksOnlyThatTabAsSelected()
    {
        // Every tab keeps its own live TaskTabView (and WebView2) in the tab template and only
        // the selected one is visible, so the selection has to be readable from each tab.
        var vm = CreateWithNewTab();
        vm.AddTaskTabCommand.Execute(null);

        Assert.False(vm.Tabs[0].IsSelected);
        Assert.True(vm.Tabs[1].IsSelected);

        vm.SelectedTab = vm.Tabs[0];

        Assert.True(vm.Tabs[0].IsSelected);
        Assert.False(vm.Tabs[1].IsSelected);
    }

    [StaFact]
    public void CloseTab_RemovesAndDisposesIt()
    {
        var vm = CreateWithNewTab();
        vm.AddTaskTabCommand.Execute(null);
        var first = vm.Tabs[0];

        vm.CloseTabCommand.Execute(first);

        Assert.Single(vm.Tabs);
        Assert.DoesNotContain(first, vm.Tabs);
    }

    [StaFact]
    public void ClosingTheLastTab_LeavesNoTabUntilNeuerTask()
    {
        // Arrange
        var vm = CreateWithNewTab();

        // Act
        vm.CloseTabCommand.Execute(vm.Tabs[0]);

        // Assert
        Assert.Empty(vm.Tabs);
        Assert.Null(vm.SelectedTab);

        vm.AddTaskTabCommand.Execute(null);

        var tab = Assert.Single(vm.Tabs);
        Assert.Same(tab, vm.SelectedTab);
        Assert.Equal("(Bezeichnung)", tab.Header);

        vm.ShutdownAll();
    }

    [StaFact]
    public void ATabsCloseRequest_ClosesIt()
    {
        var vm = CreateWithNewTab();
        vm.AddTaskTabCommand.Execute(null);
        var second = vm.Tabs[1];

        second.CloseCommand.Execute(null);

        Assert.Single(vm.Tabs);
    }

    [StaFact]
    public void StartupErrors_AreSurfaced()
    {
        var vm = Create(["Die Prompt-Datei 'review_prompt.md' ist leer."]);

        Assert.True(vm.HasStartupErrors);
        Assert.Single(vm.StartupErrors);
    }

    [StaFact]
    public void NoStartupErrors_MeansHasStartupErrorsIsFalse()
    {
        Assert.False(Create().HasStartupErrors);
    }

    [StaFact]
    public void ShutdownAll_DisposesEveryTab()
    {
        var vm = CreateWithNewTab();
        vm.AddTaskTabCommand.Execute(null);

        vm.ShutdownAll();

        Assert.Empty(vm.Tabs);
    }

    private sealed class StubScanner(params RecoverableTask[] tasks) : ITaskRecoveryScanner
    {
        /// <summary>What <see cref="Load"/> returns, keyed by task folder; any other folder has no journal.</summary>
        public Dictionary<string, RecoverableTask> Loadable { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every path set <see cref="Load"/> was asked for, in order.</summary>
        public List<TaskPaths> Loaded { get; } = [];

        /// <summary>Per <see cref="Load"/> call: whether the canonical spec and plan existed at that moment.</summary>
        public List<bool> PlanningArtefactsPresentAtLoad { get; } = [];

        /// <summary>When set, the scan stays outstanding until the test completes this task (WF_TABZUVIEL).</summary>
        public Task<IReadOnlyList<RecoverableTask>>? PendingScan { get; init; }

        public Task<IReadOnlyList<RecoverableTask>> ScanAsync(CancellationToken cancellationToken) =>
            PendingScan ?? Task.FromResult<IReadOnlyList<RecoverableTask>>(tasks);

        public RecoverableTask? Load(TaskPaths paths)
        {
            Loaded.Add(paths);
            PlanningArtefactsPresentAtLoad.Add(File.Exists(paths.SpecAbsolute) && File.Exists(paths.PlanAbsolute));
            return Loadable.TryGetValue(paths.TaskDirectory, out var task) ? task : null;
        }

        /// <summary>True exactly for the folders <see cref="Load"/> returns a task for; records nothing.</summary>
        public bool CanLoad(TaskPaths paths) => Loadable.ContainsKey(paths.TaskDirectory);
    }

    private RecoverableTask MakeRecoverable(string name)
    {
        var paths = new TaskPaths(_root, name);
        Directory.CreateDirectory(paths.TaskDirectory);

        var state = new TaskState { TaskDescription = $"d-{name}", UpdatedUtc = DateTimeOffset.UtcNow };
        foreach (var definition in PhaseCatalog.All)
        {
            state.Phases.Add(new TaskPhaseState(definition.Phase, PhaseStatus.Pending, null));
        }

        return new RecoverableTask(paths, state, WorkflowPhase.Specification);
    }

    private static readonly DateTimeOffset PhaseOneDone = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    // A task whose journal records phase 1 as done, the way 'Task laden' hands it over.
    private RecoverableTask MakeLoadable(string name)
    {
        var task = MakeRecoverable(name);
        task.State.Phases[(int)WorkflowPhase.Specification] = new TaskPhaseState(WorkflowPhase.Specification, PhaseStatus.Completed, PhaseOneDone);
        return task with { ResumePhase = WorkflowPhase.Simplifier };
    }

    [Fact]
    public async Task InitialiseAsync_AppendsOneTabPerRecoveredTaskAndSelectsTheFirst()
    {
        var shell = Create(scanner: new StubScanner(MakeRecoverable("alpha"), MakeRecoverable("beta")));

        await shell.InitialiseAsync();

        Assert.Equal(2, shell.Tabs.Count);          // no blank tab beside them
        Assert.Equal("alpha", shell.Tabs[0].TaskName);
        Assert.Equal("beta", shell.Tabs[1].TaskName);
        Assert.Same(shell.Tabs[0], shell.SelectedTab);

        shell.ShutdownAll();
    }

    [Fact]
    public async Task InitialiseAsync_NothingToRecover_LeavesNoTab()
    {
        var shell = Create();

        await shell.InitialiseAsync();

        Assert.Empty(shell.Tabs);
        Assert.Null(shell.SelectedTab);

        shell.ShutdownAll();
    }

    // --- 'Task laden' (Workflow_LOAD_AND_PAUSE spec section 5) --------------------------------

    [StaFact]
    public void LoadTask_PickerCancelled_ChangesNothing()
    {
        // Arrange - the default ScriptedPicker returns null, which is what a cancelled dialog returns.
        var scanner = new StubScanner();
        var messages = new RecordingMessages();
        var vm = CreateWithNewTab(scanner: scanner, messages: messages);
        var before = vm.SelectedTab;

        // Act
        vm.LoadTaskCommand.Execute(null);

        // Assert
        Assert.Single(vm.Tabs);
        Assert.Same(before, vm.SelectedTab);
        Assert.Empty(scanner.Loaded);
        Assert.Empty(messages.Errors);

        vm.ShutdownAll();
    }

    [StaFact]
    public void LoadTask_OpensThePickerInTheSelectedTabsWorkingDirectoryWithItsOwnCaption()
    {
        // Arrange
        var picker = new ScriptedPicker();
        var vm = CreateWithNewTab(picker: picker);
        vm.Tabs[0].WorkingDirectory = _root;

        // Act
        vm.LoadTaskCommand.Execute(null);

        // Assert
        var call = Assert.Single(picker.Calls);
        Assert.Equal(_root, call.InitialDirectory);
        Assert.Equal("Task-Ordner auswählen", call.Title);

        vm.ShutdownAll();
    }

    [StaFact]
    public void LoadTask_WithNoTabOpen_StartsThePickerInTheLastWorkingDirectoryAndSelectsTheLoadedTab()
    {
        // Arrange
        _settings.Settings.LastDirectory = _root;
        var task = MakeLoadable("alpha");
        var scanner = new StubScanner();
        scanner.Loadable[task.Paths.TaskDirectory] = task;
        var picker = new ScriptedPicker(task.Paths.TaskDirectory);
        var vm = Create(scanner: scanner, picker: picker);

        // Act
        vm.LoadTaskCommand.Execute(null);

        // Assert
        var call = Assert.Single(picker.Calls);
        Assert.Equal(_root, call.InitialDirectory);
        Assert.Equal("Task-Ordner auswählen", call.Title);
        var loaded = Assert.Single(vm.Tabs);
        Assert.Same(loaded, vm.SelectedTab);
        Assert.Equal("alpha", loaded.TaskName);

        vm.ShutdownAll();
    }

    [StaFact]
    public void LoadTask_WithNoTabOpen_AndNoValidLastDirectory_StartsThePickerWithoutAnInitialDirectory()
    {
        // Arrange - the saved directory was never created.
        _settings.Settings.LastDirectory = Path.Combine(_root, "missing");
        var task = MakeLoadable("alpha");
        var scanner = new StubScanner();
        scanner.Loadable[task.Paths.TaskDirectory] = task;
        var picker = new ScriptedPicker(task.Paths.TaskDirectory);
        var vm = Create(scanner: scanner, picker: picker);

        // Act
        vm.LoadTaskCommand.Execute(null);

        // Assert
        var call = Assert.Single(picker.Calls);
        Assert.Null(call.InitialDirectory);
        var loaded = Assert.Single(vm.Tabs);
        Assert.Same(loaded, vm.SelectedTab);

        vm.ShutdownAll();
    }

    [StaFact]
    public void LoadTask_TaskFolderWithAJournal_AddsALockedTabShowingTheJournalAndSelectsIt()
    {
        // Arrange
        var task = MakeLoadable("alpha");
        var scanner = new StubScanner();
        scanner.Loadable[task.Paths.TaskDirectory] = task;
        var vm = CreateWithNewTab(scanner: scanner, picker: new ScriptedPicker(task.Paths.TaskDirectory));

        // Act
        vm.LoadTaskCommand.Execute(null);

        // Assert
        Assert.Equal(2, vm.Tabs.Count);
        var loaded = vm.Tabs[1];
        Assert.Same(loaded, vm.SelectedTab);
        Assert.Equal("alpha", loaded.TaskName);
        Assert.Equal(_root, loaded.WorkingDirectory);
        Assert.True(loaded.IsNameLocked);
        Assert.True(loaded.IsRecovered);
        Assert.Equal(PhaseStatus.Completed, loaded.Phases[(int)WorkflowPhase.Specification].Status);
        Assert.Equal(PhaseStatus.Pending, loaded.Phases[(int)WorkflowPhase.Simplifier].Status);
        Assert.Equal("Continue workflow", loaded.StartButtonLabel);

        vm.ShutdownAll();
    }

    [StaFact]
    public void LoadTask_ALoadedTabsCloseButton_ClosesIt()
    {
        // Arrange
        var task = MakeLoadable("alpha");
        var scanner = new StubScanner();
        scanner.Loadable[task.Paths.TaskDirectory] = task;
        var vm = CreateWithNewTab(scanner: scanner, picker: new ScriptedPicker(task.Paths.TaskDirectory));
        vm.LoadTaskCommand.Execute(null);

        // Act - the tab's own close request must reach the shell, as for every other tab.
        vm.Tabs[1].CloseCommand.Execute(null);

        // Assert
        Assert.Single(vm.Tabs);

        vm.ShutdownAll();
    }

    [StaFact]
    public void LoadTask_SameFolderAgain_SelectsTheOpenTabWithoutReadingTheJournalAgain()
    {
        // Arrange - the later picks differ only in casing and a trailing separator, and in a '.' segment.
        var task = MakeLoadable("alpha");
        var scanner = new StubScanner();
        scanner.Loadable[task.Paths.TaskDirectory] = task;
        var picker = new ScriptedPicker(
            task.Paths.TaskDirectory,
            task.Paths.TaskDirectory.ToUpperInvariant() + Path.DirectorySeparatorChar,
            Path.Combine(_root, ".", "alpha"));
        var vm = CreateWithNewTab(scanner: scanner, picker: picker);
        vm.LoadTaskCommand.Execute(null);
        vm.SelectedTab = vm.Tabs[0];

        // Act
        vm.LoadTaskCommand.Execute(null);
        var afterCasing = vm.SelectedTab;
        vm.SelectedTab = vm.Tabs[0];
        vm.LoadTaskCommand.Execute(null);

        // Assert
        Assert.Equal(2, vm.Tabs.Count);
        Assert.Same(vm.Tabs[1], afterCasing);
        Assert.Same(vm.Tabs[1], vm.SelectedTab);
        Assert.Single(scanner.Loaded);

        vm.ShutdownAll();
    }

    [StaFact]
    public void LoadTask_TaskAlreadyTypedIntoATab_SelectsThatTabWithoutReadingTheJournal()
    {
        // Arrange
        var scanner = new StubScanner();
        var vm = CreateWithNewTab(scanner: scanner, picker: new ScriptedPicker(Path.Combine(_root, "alpha")));
        vm.Tabs[0].WorkingDirectory = _root;
        vm.Tabs[0].TaskName = "alpha";
        vm.AddTaskTabCommand.Execute(null);

        // Act
        vm.LoadTaskCommand.Execute(null);

        // Assert
        Assert.Equal(2, vm.Tabs.Count);
        Assert.Same(vm.Tabs[0], vm.SelectedTab);
        Assert.Empty(scanner.Loaded);

        vm.ShutdownAll();
    }

    [StaFact]
    public void LoadTask_TypedTabWithAnUncanonicalSpelling_IsRecognisedAsOpen()
    {
        // Arrange - the typed working directory carries a '.' segment and '/' separators.
        var scanner = new StubScanner();
        var vm = CreateWithNewTab(scanner: scanner, picker: new ScriptedPicker(Path.Combine(_root, "alpha")));
        vm.Tabs[0].WorkingDirectory = _root.Replace('\\', '/') + "/.";
        vm.Tabs[0].TaskName = "alpha";
        vm.AddTaskTabCommand.Execute(null);

        // Act
        vm.LoadTaskCommand.Execute(null);

        // Assert
        Assert.Equal(2, vm.Tabs.Count);
        Assert.Same(vm.Tabs[0], vm.SelectedTab);
        Assert.Empty(scanner.Loaded);

        vm.ShutdownAll();
    }

    [StaFact]
    public void LoadTask_TaskRunningInAnotherTab_SelectsItWithoutReadingTheJournal()
    {
        // Arrange - Review Focus 1: the open tab's run may be writing the journal right now, so Load
        // must not even be asked (spec 5.3, decision D4).
        var scanner = new StubScanner();
        var vm = CreateWithNewTab(scanner: scanner, picker: new ScriptedPicker(Path.Combine(_root, "alpha")));
        var running = vm.Tabs[0];
        running.WorkingDirectory = _root;
        running.TaskName = "alpha";
        running.StartWorkflowCommand.Execute(null);
        vm.AddTaskTabCommand.Execute(null);

        // Act
        vm.LoadTaskCommand.Execute(null);

        // Assert
        Assert.True(running.IsRunning);
        Assert.Equal(2, vm.Tabs.Count);
        Assert.Same(running, vm.SelectedTab);
        Assert.Empty(scanner.Loaded);

        vm.ShutdownAll();
    }

    [StaFact]
    public void LoadTask_DriveRoot_ShowsTheNotATaskFolderErrorWithoutReadingAJournal()
    {
        // Arrange
        var driveRoot = Path.GetPathRoot(_root) ?? @"C:\";
        var scanner = new StubScanner();
        var messages = new RecordingMessages();
        var vm = CreateWithNewTab(scanner: scanner, picker: new ScriptedPicker(driveRoot), messages: messages);

        // Act
        vm.LoadTaskCommand.Execute(null);

        // Assert
        var error = Assert.Single(messages.Errors);
        Assert.Equal(
            $"'{driveRoot}' kann kein Task-Ordner sein. Bitte den Ordner eines Tasks innerhalb eines Arbeitsverzeichnisses auswählen.",
            error);
        Assert.Single(vm.Tabs);
        Assert.Empty(scanner.Loaded);

        vm.ShutdownAll();
    }

    [StaFact]
    public void LoadTask_LoadFindsNothing_ShowsTheUnreadableJournalErrorAndAddsNoTab()
    {
        // Arrange - the stub returns null, as Load does for an unreadable journal or a failed write
        // (Workflow_LoadFix spec 5.5). The folder holds no Markdown file, so nothing is offered.
        var folder = Path.Combine(_root, "plain");
        Directory.CreateDirectory(folder);
        var scanner = new StubScanner();
        var messages = new RecordingMessages();
        var vm = CreateWithNewTab(scanner: scanner, picker: new ScriptedPicker(folder), messages: messages);
        var before = vm.SelectedTab;

        // Act
        vm.LoadTaskCommand.Execute(null);

        // Assert
        var error = Assert.Single(messages.Errors);
        Assert.Equal(
            $"Im Ordner '{folder}' konnte kein Workflow-Task geladen werden: Die Datei .workflow-state.json ist nicht lesbar oder konnte nicht angelegt werden.",
            error);
        Assert.Empty(messages.Questions);
        Assert.Single(vm.Tabs);
        Assert.Same(before, vm.SelectedTab);
        Assert.Single(scanner.Loaded);

        vm.ShutdownAll();
    }

    [Fact]
    public async Task InitialiseAsync_SkipsARecoveredTaskThatIsAlreadyOpen()
    {
        // Arrange - the user loads alpha while the startup scan is still running; the scan then
        // reports alpha as well.
        var alpha = MakeLoadable("alpha");
        var scanner = new StubScanner(MakeRecoverable("alpha"), MakeRecoverable("beta"));
        scanner.Loadable[alpha.Paths.TaskDirectory] = alpha;
        var shell = Create(scanner: scanner, picker: new ScriptedPicker(alpha.Paths.TaskDirectory));
        shell.LoadTaskCommand.Execute(null);

        // Act
        await shell.InitialiseAsync();

        // Assert - the loaded alpha and the recovered beta; never a second alpha.
        Assert.Equal(2, shell.Tabs.Count);
        Assert.Single(shell.Tabs, tab => string.Equals(tab.TaskName, "alpha", StringComparison.Ordinal));
        Assert.Equal("beta", shell.Tabs[1].TaskName);

        shell.ShutdownAll();
    }

    [StaFact]
    public async Task InitialiseAsync_ATabOpenedWithNeuerTaskDuringTheScan_StaysFirstAndTheFirstRecoveredIsSelected()
    {
        // Arrange
        var pending = new TaskCompletionSource<IReadOnlyList<RecoverableTask>>();
        var alpha = MakeRecoverable("alpha");
        var beta = MakeRecoverable("beta");
        var shell = Create(scanner: new StubScanner { PendingScan = pending.Task });

        // Act - 'Neuer Task' is pressed while the startup scan is still running.
        var init = shell.InitialiseAsync();
        shell.AddTaskTabCommand.Execute(null);
        var x = shell.SelectedTab;
        pending.SetResult([alpha, beta]);
        await init;

        // Assert
        Assert.NotNull(x);
        Assert.Equal(3, shell.Tabs.Count);
        Assert.Same(x, shell.Tabs[0]);
        Assert.Equal("alpha", shell.Tabs[1].TaskName);
        Assert.Equal("beta", shell.Tabs[2].TaskName);
        Assert.Same(shell.Tabs[1], shell.SelectedTab);

        shell.ShutdownAll();
    }

    /// <summary>Waits, bounded, for a condition the tab's own continuation has to establish.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    private TaskTabViewModel StartAHeldRun(MainWindowViewModel shell, HeldRunOrchestrator orchestrator)
    {
        var tab = shell.Tabs[0];
        tab.WorkingDirectory = _root;
        tab.TaskName = "held";
        tab.PauseCommand.Execute(null);
        tab.StartWorkflowCommand.Execute(null);
        Assert.NotNull(orchestrator.RunTask);
        Assert.False(orchestrator.RunTask.IsCompleted);
        Assert.True(tab.IsRunning);
        return tab;
    }

    [StaFact]
    public async Task CloseTab_WhileItsRunIsHeld_EndsTheRunQuietly()
    {
        // Arrange
        var orchestrator = new HeldRunOrchestrator();
        var shell = CreateWithNewTab(orchestrator: orchestrator);
        var tab = StartAHeldRun(shell, orchestrator);

        // Act
        tab.CloseCommand.Execute(null);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await orchestrator.RunTask!.WaitAsync(TimeSpan.FromSeconds(10)));
        await WaitUntilAsync(() => !tab.IsRunning);
        Assert.False(tab.IsRunning);
        Assert.Null(tab.ValidationMessage);

        shell.ShutdownAll();
    }

    [StaFact]
    public async Task ShutdownAll_WhileARunIsHeld_EndsTheRunQuietly()
    {
        // Arrange
        var orchestrator = new HeldRunOrchestrator();
        var shell = CreateWithNewTab(orchestrator: orchestrator);
        var tab = StartAHeldRun(shell, orchestrator);

        // Act
        shell.ShutdownAll();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await orchestrator.RunTask!.WaitAsync(TimeSpan.FromSeconds(10)));
        await WaitUntilAsync(() => !tab.IsRunning);
        Assert.False(tab.IsRunning);
        Assert.Null(tab.ValidationMessage);
    }

    // --- Workflow_LoadFix spec section 6: adopting a misnamed spec and plan ----------------

    private const string MisnamedSpec = "2026-09-30-alpha-design.md";
    private const string MisnamedPlan = "2026-09-30-alpha.md";

    /// <summary>A loadable task whose spec and plan carry foreign names, as VSDM2's did.</summary>
    private RecoverableTask MisnamedTask(StubScanner scanner)
    {
        var task = MakeLoadable("alpha");
        File.WriteAllText(Path.Combine(task.Paths.TaskDirectory, MisnamedSpec), "die Spezifikation");
        File.WriteAllText(Path.Combine(task.Paths.TaskDirectory, MisnamedPlan), "der Plan");
        scanner.Loadable[task.Paths.TaskDirectory] = task;
        return task;
    }

    [StaFact]
    public void LoadTask_MisnamedSpecAndPlan_Ja_RenamesBothBeforeLoading()
    {
        // Arrange
        var scanner = new StubScanner();
        var task = MisnamedTask(scanner);
        var folder = task.Paths.TaskDirectory;
        var messages = new RecordingMessages(true);
        var files = new ScriptedFilePicker(Path.Combine(folder, MisnamedSpec), Path.Combine(folder, MisnamedPlan));
        var vm = CreateWithNewTab(scanner: scanner, picker: new ScriptedPicker(folder), messages: messages, filePicker: files);

        // Act
        vm.LoadTaskCommand.Execute(null);

        // Assert
        var question = Assert.Single(messages.Questions);
        Assert.Equal(
            $"Im Ordner '{folder}' fehlen die Standarddateien des Tasks: alpha_spec.md, alpha_plan.md.\n\nVorhandene Dateien auswählen und in diese Namen umbenennen? Bei 'Nein' wird der Task ohne Umbenennen geladen.",
            question);
        Assert.Equal(
            new[]
            {
                ((string?)folder, "Spezifikation auswählen (wird zu alpha_spec.md)"),
                ((string?)folder, "Plan auswählen (wird zu alpha_plan.md)"),
            },
            files.Calls);
        Assert.Equal("die Spezifikation", File.ReadAllText(task.Paths.SpecAbsolute));
        Assert.Equal("der Plan", File.ReadAllText(task.Paths.PlanAbsolute));
        Assert.False(File.Exists(Path.Combine(folder, MisnamedSpec)));
        Assert.False(File.Exists(Path.Combine(folder, MisnamedPlan)));
        Assert.Equal(new[] { true }, scanner.PlanningArtefactsPresentAtLoad);
        Assert.Empty(messages.Errors);
        Assert.Equal(2, vm.Tabs.Count);

        vm.ShutdownAll();
    }

    [StaFact]
    public void LoadTask_MisnamedSpecAndPlan_Nein_LoadsWithoutRenaming()
    {
        // Arrange
        var scanner = new StubScanner();
        var task = MisnamedTask(scanner);
        var messages = new RecordingMessages(false);
        var files = new ScriptedFilePicker();
        var vm = CreateWithNewTab(scanner: scanner, picker: new ScriptedPicker(task.Paths.TaskDirectory), messages: messages, filePicker: files);

        // Act
        vm.LoadTaskCommand.Execute(null);

        // Assert
        Assert.Single(messages.Questions);
        Assert.Empty(files.Calls);
        Assert.True(File.Exists(Path.Combine(task.Paths.TaskDirectory, MisnamedSpec)));
        Assert.False(File.Exists(task.Paths.SpecAbsolute));
        Assert.Single(scanner.Loaded);
        Assert.Equal(2, vm.Tabs.Count);

        vm.ShutdownAll();
    }

    [StaFact]
    public void LoadTask_MisnamedSpecAndPlan_FileDialogCancelled_RenamesAndLoadsNothing()
    {
        // Arrange - the spec is picked, then the plan dialog is cancelled.
        var scanner = new StubScanner();
        var task = MisnamedTask(scanner);
        var messages = new RecordingMessages(true);
        var files = new ScriptedFilePicker(Path.Combine(task.Paths.TaskDirectory, MisnamedSpec));
        var vm = CreateWithNewTab(scanner: scanner, picker: new ScriptedPicker(task.Paths.TaskDirectory), messages: messages, filePicker: files);

        // Act
        vm.LoadTaskCommand.Execute(null);

        // Assert - cancelling means "stop", and nothing has been written.
        Assert.Equal(2, files.Calls.Count);
        Assert.True(File.Exists(Path.Combine(task.Paths.TaskDirectory, MisnamedSpec)));
        Assert.False(File.Exists(task.Paths.SpecAbsolute));
        Assert.Empty(scanner.Loaded);
        Assert.Empty(messages.Errors);
        Assert.Single(vm.Tabs);

        vm.ShutdownAll();
    }

    [StaFact]
    public void LoadTask_OnlyThePlanMissing_AsksOnlyForThePlan()
    {
        // Arrange
        var scanner = new StubScanner();
        var task = MakeLoadable("alpha");
        scanner.Loadable[task.Paths.TaskDirectory] = task;
        File.WriteAllText(task.Paths.SpecAbsolute, "die Spezifikation");
        var notes = Path.Combine(task.Paths.TaskDirectory, "notes.md");
        File.WriteAllText(notes, "der Plan");
        var messages = new RecordingMessages(true);
        var files = new ScriptedFilePicker(notes);
        var vm = Create(scanner: scanner, picker: new ScriptedPicker(task.Paths.TaskDirectory), messages: messages, filePicker: files);

        // Act
        vm.LoadTaskCommand.Execute(null);

        // Assert
        var question = Assert.Single(messages.Questions);
        Assert.Contains("fehlen die Standarddateien des Tasks: alpha_plan.md.", question, StringComparison.Ordinal);
        var call = Assert.Single(files.Calls);
        Assert.Equal("Plan auswählen (wird zu alpha_plan.md)", call.Title);
        Assert.Equal("der Plan", File.ReadAllText(task.Paths.PlanAbsolute));
        Assert.Equal("die Spezifikation", File.ReadAllText(task.Paths.SpecAbsolute));
        Assert.Single(scanner.Loaded);

        vm.ShutdownAll();
    }

    [StaFact]
    public void LoadTask_SameFileForSpecAndPlan_IsRefusedAndNothingIsRenamed()
    {
        // Arrange - Review Focus 3: the second pick differs only in casing.
        var scanner = new StubScanner();
        var task = MisnamedTask(scanner);
        var spec = Path.Combine(task.Paths.TaskDirectory, MisnamedSpec);
        var messages = new RecordingMessages(true);
        var files = new ScriptedFilePicker(spec, spec.ToUpperInvariant());
        var vm = Create(scanner: scanner, picker: new ScriptedPicker(task.Paths.TaskDirectory), messages: messages, filePicker: files);

        // Act
        vm.LoadTaskCommand.Execute(null);

        // Assert
        var error = Assert.Single(messages.Errors);
        Assert.Equal("Spezifikation und Plan müssen zwei verschiedene Dateien sein.", error);
        Assert.True(File.Exists(spec));
        Assert.False(File.Exists(task.Paths.SpecAbsolute));
        Assert.Empty(scanner.Loaded);

        vm.ShutdownAll();
    }

    [StaFact]
    public void LoadTask_ChosenFileIsAlreadyATaskFile_IsRefused()
    {
        // Arrange - the plan is missing and the user picks the task's own spec for it.
        var scanner = new StubScanner();
        var task = MakeLoadable("alpha");
        scanner.Loadable[task.Paths.TaskDirectory] = task;
        File.WriteAllText(task.Paths.SpecAbsolute, "die Spezifikation");
        File.WriteAllText(Path.Combine(task.Paths.TaskDirectory, "notes.md"), "x");
        var messages = new RecordingMessages(true);
        var files = new ScriptedFilePicker(task.Paths.SpecAbsolute);
        var vm = Create(scanner: scanner, picker: new ScriptedPicker(task.Paths.TaskDirectory), messages: messages, filePicker: files);

        // Act
        vm.LoadTaskCommand.Execute(null);

        // Assert
        var error = Assert.Single(messages.Errors);
        Assert.Equal($"'{task.Paths.SpecAbsolute}' ist bereits eine Datei des Tasks und kann nicht umbenannt werden.", error);
        Assert.True(File.Exists(task.Paths.SpecAbsolute));
        Assert.False(File.Exists(task.Paths.PlanAbsolute));
        Assert.Empty(scanner.Loaded);

        vm.ShutdownAll();
    }

    [StaFact]
    public void LoadTask_ChosenFileIsLocked_ShowsTheErrorAndLoadsNothing()
    {
        // Arrange
        var scanner = new StubScanner();
        var task = MisnamedTask(scanner);
        var spec = Path.Combine(task.Paths.TaskDirectory, MisnamedSpec);
        var messages = new RecordingMessages(true);
        var files = new ScriptedFilePicker(spec, Path.Combine(task.Paths.TaskDirectory, MisnamedPlan));
        var vm = CreateWithNewTab(scanner: scanner, picker: new ScriptedPicker(task.Paths.TaskDirectory), messages: messages, filePicker: files);

        // Act - an editor holds the spec open without sharing it.
        using (new FileStream(spec, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            vm.LoadTaskCommand.Execute(null);
        }

        // Assert
        var error = Assert.Single(messages.Errors);
        Assert.StartsWith("Die Datei konnte nicht umbenannt werden: ", error, StringComparison.Ordinal);
        Assert.True(File.Exists(spec));
        Assert.False(File.Exists(task.Paths.SpecAbsolute));
        Assert.Empty(scanner.Loaded);
        Assert.Single(vm.Tabs);

        vm.ShutdownAll();
    }

    [StaFact]
    public void LoadTask_CanonicalSpecAndPlanPresent_AsksNothing()
    {
        // Arrange
        var scanner = new StubScanner();
        var task = MakeLoadable("alpha");
        scanner.Loadable[task.Paths.TaskDirectory] = task;
        File.WriteAllText(task.Paths.SpecAbsolute, "s");
        File.WriteAllText(task.Paths.PlanAbsolute, "p");
        File.WriteAllText(Path.Combine(task.Paths.TaskDirectory, "notes.md"), "n");
        var messages = new RecordingMessages(true);
        var files = new ScriptedFilePicker();
        var vm = CreateWithNewTab(scanner: scanner, picker: new ScriptedPicker(task.Paths.TaskDirectory), messages: messages, filePicker: files);

        // Act
        vm.LoadTaskCommand.Execute(null);

        // Assert
        Assert.Empty(messages.Questions);
        Assert.Empty(files.Calls);
        Assert.Equal(2, vm.Tabs.Count);

        vm.ShutdownAll();
    }

    [StaFact]
    public void LoadTask_SpecChosenFromAnotherFolder_IsMovedIntoTheTaskFolder()
    {
        // Arrange - Review Focus 2: a spec left where a brainstorming session wrote it.
        var scanner = new StubScanner();
        var task = MakeLoadable("alpha");
        scanner.Loadable[task.Paths.TaskDirectory] = task;
        var elsewhere = Path.Combine(_root, "docs");
        Directory.CreateDirectory(elsewhere);
        var spec = Path.Combine(elsewhere, MisnamedSpec);
        File.WriteAllText(spec, "die Spezifikation");
        var plan = Path.Combine(task.Paths.TaskDirectory, MisnamedPlan);
        File.WriteAllText(plan, "der Plan");
        var messages = new RecordingMessages(true);
        var files = new ScriptedFilePicker(spec, plan);
        var vm = Create(scanner: scanner, picker: new ScriptedPicker(task.Paths.TaskDirectory), messages: messages, filePicker: files);

        // Act
        vm.LoadTaskCommand.Execute(null);

        // Assert
        Assert.Equal("die Spezifikation", File.ReadAllText(task.Paths.SpecAbsolute));
        Assert.False(File.Exists(spec));
        Assert.Equal("der Plan", File.ReadAllText(task.Paths.PlanAbsolute));
        Assert.Empty(messages.Errors);

        vm.ShutdownAll();
    }

    [StaFact]
    public void LoadTask_TaskAlreadyOpen_AsksNothingAndRenamesNothing()
    {
        // Arrange - Review Focus 4: the open tab may be running; its files are not touched.
        var scanner = new StubScanner();
        var messages = new RecordingMessages(true);
        var files = new ScriptedFilePicker();
        var vm = CreateWithNewTab(scanner: scanner, picker: new ScriptedPicker(Path.Combine(_root, "alpha")), messages: messages, filePicker: files);
        vm.Tabs[0].WorkingDirectory = _root;
        vm.Tabs[0].TaskName = "alpha";
        var folder = Path.Combine(_root, "alpha");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, MisnamedSpec), "die Spezifikation");
        vm.AddTaskTabCommand.Execute(null);

        // Act
        vm.LoadTaskCommand.Execute(null);

        // Assert
        Assert.Same(vm.Tabs[0], vm.SelectedTab);
        Assert.Empty(messages.Questions);
        Assert.Empty(files.Calls);
        Assert.True(File.Exists(Path.Combine(folder, MisnamedSpec)));
        Assert.Empty(scanner.Loaded);

        vm.ShutdownAll();
    }

    [StaFact]
    public void LoadTask_JournalUnreadable_AsksNothingRenamesNothingAndShowsTheLoadError()
    {
        // Arrange - AC-7 "preflight fails": two candidates exist, but the journal cannot be read, so
        // Load cannot succeed (Workflow_LoadFix spec 6.2, D21). The folder is not in Loadable, so the
        // stub's CanLoad is false and its Load returns null.
        var folder = Path.Combine(_root, "alpha");
        Directory.CreateDirectory(folder);
        var spec = Path.Combine(folder, MisnamedSpec);
        var plan = Path.Combine(folder, MisnamedPlan);
        File.WriteAllText(spec, "die Spezifikation");
        File.WriteAllText(plan, "der Plan");
        File.WriteAllText(Path.Combine(folder, ".workflow-state.json"), "{ this is not json");
        var scanner = new StubScanner();
        var messages = new RecordingMessages(true);
        var files = new ScriptedFilePicker(spec, plan);
        var vm = CreateWithNewTab(scanner: scanner, picker: new ScriptedPicker(folder), messages: messages, filePicker: files);

        // Act
        vm.LoadTaskCommand.Execute(null);

        // Assert - a user's file is never renamed for a load that cannot succeed.
        Assert.Empty(messages.Questions);
        Assert.Empty(files.Calls);
        Assert.Equal("die Spezifikation", File.ReadAllText(spec));
        Assert.Equal("der Plan", File.ReadAllText(plan));
        Assert.False(File.Exists(Path.Combine(folder, "alpha_spec.md")));
        Assert.False(File.Exists(Path.Combine(folder, "alpha_plan.md")));
        Assert.Single(scanner.Loaded);
        var error = Assert.Single(messages.Errors);
        Assert.Equal(
            $"Im Ordner '{folder}' konnte kein Workflow-Task geladen werden: Die Datei .workflow-state.json ist nicht lesbar oder konnte nicht angelegt werden.",
            error);
        Assert.Single(vm.Tabs);

        vm.ShutdownAll();
    }

    [StaFact]
    public void LoadTask_ZeroByteCanonicalSpec_IsOfferedAndReplaced()
    {
        // Arrange - AC-7, F25: a 0-byte canonical spec counts as missing and is replaced by the pick.
        var scanner = new StubScanner();
        var task = MakeLoadable("alpha");
        scanner.Loadable[task.Paths.TaskDirectory] = task;
        File.WriteAllBytes(task.Paths.SpecAbsolute, []);
        File.WriteAllText(task.Paths.PlanAbsolute, "der Plan");
        var draft = Path.Combine(task.Paths.TaskDirectory, "entwurf-design.md");
        File.WriteAllText(draft, "die Spezifikation");
        var messages = new RecordingMessages(true);
        var files = new ScriptedFilePicker(draft);
        var vm = Create(scanner: scanner, picker: new ScriptedPicker(task.Paths.TaskDirectory), messages: messages, filePicker: files);

        // Act
        vm.LoadTaskCommand.Execute(null);

        // Assert - content and source prove the replacement; PlanningArtefactsPresentAtLoad cannot,
        // because the 0-byte placeholder exists with or without it.
        var question = Assert.Single(messages.Questions);
        Assert.Contains("fehlen die Standarddateien des Tasks: alpha_spec.md.", question, StringComparison.Ordinal);
        var call = Assert.Single(files.Calls);
        Assert.Equal("Spezifikation auswählen (wird zu alpha_spec.md)", call.Title);
        Assert.Equal("die Spezifikation", File.ReadAllText(task.Paths.SpecAbsolute));
        Assert.False(File.Exists(draft));
        Assert.Equal("der Plan", File.ReadAllText(task.Paths.PlanAbsolute));
        Assert.Single(scanner.Loaded);
        Assert.Empty(messages.Errors);

        vm.ShutdownAll();
    }

    // --- Workflow_LoadFix spec 6.3 step 5 (D6): a pair is renamed completely or not at all -----

    /// <summary>Every file below the folder as (relative path, SHA-256), ordinally sorted.</summary>
    private static List<(string Name, string Hash)> Snapshot(string folder) =>
        Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .Select(file => (
                Path.GetRelativePath(folder, file),
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))))
            .OrderBy(entry => entry.Item1, StringComparer.Ordinal)
            .ToList();

    [StaFact]
    public void LoadTask_SecondMoveFails_UndoesTheFirstAndLeavesEveryFileAsItWas()
    {
        // Arrange - the real folder service; the spec moves, the locked plan cannot.
        var scanner = new StubScanner();
        var task = MisnamedTask(scanner);
        var folder = task.Paths.TaskDirectory;
        var spec = Path.Combine(folder, MisnamedSpec);
        var plan = Path.Combine(folder, MisnamedPlan);
        var before = Snapshot(folder);
        var messages = new RecordingMessages(true);
        var files = new ScriptedFilePicker(spec, plan);
        var vm = CreateWithNewTab(scanner: scanner, picker: new ScriptedPicker(folder), messages: messages, filePicker: files);

        // Act - an editor holds the plan open without sharing it.
        using (new FileStream(plan, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            vm.LoadTaskCommand.Execute(null);
        }

        // Assert - the spec is back under its old name, and the undo itself succeeded.
        Assert.Equal(before, Snapshot(folder));
        Assert.False(File.Exists(task.Paths.SpecAbsolute));
        var error = Assert.Single(messages.Errors);
        Assert.StartsWith("Die Datei konnte nicht umbenannt werden: ", error, StringComparison.Ordinal);
        Assert.DoesNotContain("zurückbenannt", error, StringComparison.Ordinal);
        Assert.Empty(scanner.Loaded);
        Assert.Single(vm.Tabs);

        vm.ShutdownAll();
    }

    [StaFact]
    public void LoadTask_UndoFails_NamesTheFileThatCouldNotBeMovedBack()
    {
        // Arrange - call 2 (the plan's move) fails, then call 3 (the spec's undo) fails as well.
        var scanner = new StubScanner();
        var task = MisnamedTask(scanner);
        var spec = task.Paths.SpecAbsolute;
        var source = Path.Combine(task.Paths.TaskDirectory, MisnamedSpec);
        var plan = Path.Combine(task.Paths.TaskDirectory, MisnamedPlan);
        var folders = new ScriptedFolderService();
        folders.Failures[2] = new IOException("zweiter Fehler");
        folders.Failures[3] = new IOException("Rückbenennung gescheitert");
        var messages = new RecordingMessages(true);
        var files = new ScriptedFilePicker(source, plan);
        var vm = CreateWithNewTab(scanner: scanner, picker: new ScriptedPicker(task.Paths.TaskDirectory), messages: messages, filePicker: files, folders: folders);

        // Act
        vm.LoadTaskCommand.Execute(null);

        // Assert
        Assert.Equal(
            new[] { (source, spec), (plan, task.Paths.PlanAbsolute), (spec, source) },
            folders.AdoptCalls);
        var error = Assert.Single(messages.Errors);
        Assert.Equal(
            $"Die Datei konnte nicht umbenannt werden: zweiter Fehler\n\n'{spec}' konnte nicht nach '{source}' zurückbenannt werden: Rückbenennung gescheitert",
            error);
        Assert.Empty(scanner.Loaded);
        Assert.Single(vm.Tabs);

        vm.ShutdownAll();
    }

    [StaFact]
    public void LoadTask_SecondMoveDenied_ShowsTheAccessMessageAndUndoesTheFirst()
    {
        // Arrange
        var scanner = new StubScanner();
        var task = MisnamedTask(scanner);
        var source = Path.Combine(task.Paths.TaskDirectory, MisnamedSpec);
        var folders = new ScriptedFolderService();
        folders.Failures[2] = new UnauthorizedAccessException("verweigert");
        var messages = new RecordingMessages(true);
        var files = new ScriptedFilePicker(source, Path.Combine(task.Paths.TaskDirectory, MisnamedPlan));
        var vm = CreateWithNewTab(scanner: scanner, picker: new ScriptedPicker(task.Paths.TaskDirectory), messages: messages, filePicker: files, folders: folders);

        // Act
        vm.LoadTaskCommand.Execute(null);

        // Assert
        var error = Assert.Single(messages.Errors);
        Assert.Equal("Kein Zugriff beim Umbenennen: verweigert", error);
        Assert.Equal("die Spezifikation", File.ReadAllText(source));
        Assert.False(File.Exists(task.Paths.SpecAbsolute));
        Assert.Empty(scanner.Loaded);
        Assert.Single(vm.Tabs);

        vm.ShutdownAll();
    }

    /// <summary>
    /// WF_QUEUE: the runs of the named tasks journal every phase before Implementation as Completed and
    /// Implementation as given, then return - a finished task, or one that stopped short. Every other run
    /// holds until it is cancelled. Records the task names in start order.
    /// </summary>
    private sealed class QueueOrchestrator(FakeTaskStateStore store, PhaseStatus implementation, params string[] ending)
        : IWorkflowOrchestrator
    {
        public List<string> Started { get; } = [];

        public Task RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            Started.Add(request.Paths.TaskName);
            if (!ending.Contains(request.Paths.TaskName, StringComparer.Ordinal))
            {
                return Task.Delay(Timeout.Infinite, cancellationToken);
            }

            foreach (var phase in PhaseCatalog.All)
            {
                store.RecordPhase(
                    request.Paths,
                    phase.Phase,
                    phase.Phase == WorkflowPhase.Implementation ? implementation : PhaseStatus.Completed);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>'Neuer Task', then the name and working directory a user would type.</summary>
    private TaskTabViewModel OpenNamedTab(MainWindowViewModel shell, string name)
    {
        shell.AddTaskTabCommand.Execute(null);
        var tab = shell.Tabs[^1];
        tab.WorkingDirectory = _root;
        tab.TaskName = name;
        return tab;
    }

    /// <summary>
    /// What the view's Selector does (TaskTabView.xaml, TwoWay SelectedItem): when the selected tab leaves
    /// QueueCandidates it writes null into QueuedAfter (WF_QUEUE spec Q8, Q11).
    /// </summary>
    private static void EmulateSelector(TaskTabViewModel tab) =>
        tab.QueueCandidates.CollectionChanged += (_, e) =>
        {
            if (tab.QueuedAfter is { } selected && e.OldItems?.Contains(selected) == true)
            {
                tab.QueuedAfter = null;
            }
        };

    // --- Queue (WF_QUEUE spec section 4) ---

    [StaFact]
    public void QueueCandidates_ListEveryOtherOpenTabInTabOrder()
    {
        // Arrange
        var shell = Create();
        var alpha = OpenNamedTab(shell, "alpha");
        var beta = OpenNamedTab(shell, "beta");
        var gamma = OpenNamedTab(shell, "gamma");

        // Act
        var before = (alpha.QueueCandidates.ToList(), beta.QueueCandidates.ToList(), gamma.QueueCandidates.ToList());
        shell.CloseTabCommand.Execute(beta);

        // Assert
        Assert.Equal(new[] { beta, gamma }, before.Item1);
        Assert.Equal(new[] { alpha, gamma }, before.Item2);
        Assert.Equal(new[] { alpha, beta }, before.Item3);
        Assert.Equal(new[] { gamma }, alpha.QueueCandidates);
        Assert.Equal(new[] { alpha }, gamma.QueueCandidates);

        shell.ShutdownAll();
    }

    [StaFact]
    public async Task QueueCandidates_IncludeRecoveredTabs()
    {
        // Arrange
        var shell = Create(scanner: new StubScanner(MakeRecoverable("alpha"), MakeRecoverable("beta")));
        shell.AddTaskTabCommand.Execute(null);
        var fresh = shell.Tabs[0];

        // Act
        await shell.InitialiseAsync();

        // Assert
        Assert.Equal(new[] { shell.Tabs[1], shell.Tabs[2] }, fresh.QueueCandidates);
        Assert.Equal(new[] { fresh, shell.Tabs[2] }, shell.Tabs[1].QueueCandidates);
        Assert.Equal(new[] { fresh, shell.Tabs[1] }, shell.Tabs[2].QueueCandidates);

        shell.ShutdownAll();
    }

    [StaFact]
    public void AQueuedTab_StartsWhenItsPredecessorFinishes_AndThePredecessorLeavesEveryList()
    {
        // Arrange
        var store = new FakeTaskStateStore();
        var orchestrator = new QueueOrchestrator(store, PhaseStatus.Completed, "alpha");
        var shell = Create(orchestrator: orchestrator, stateStore: store);
        var alpha = OpenNamedTab(shell, "alpha");
        var beta = OpenNamedTab(shell, "beta");
        var gamma = OpenNamedTab(shell, "gamma");
        EmulateSelector(beta);
        beta.QueuedAfter = alpha;

        // Act
        alpha.StartWorkflowCommand.Execute(null);

        // Assert
        Assert.True(alpha.IsTaskFinished);
        Assert.True(beta.IsRunning);
        Assert.Null(beta.QueuedAfter);
        Assert.False(gamma.IsRunning);
        Assert.Equal(new[] { "alpha", "beta" }, orchestrator.Started);
        Assert.Equal(new[] { gamma }, beta.QueueCandidates);
        Assert.Equal(new[] { beta }, gamma.QueueCandidates);

        shell.ShutdownAll();
    }

    [StaFact]
    public void AQueuedTab_StaysQueuedWhenItsPredecessorStopsShortOfImplementation()
    {
        // Arrange
        var store = new FakeTaskStateStore();
        var orchestrator = new QueueOrchestrator(store, PhaseStatus.Active, "alpha");
        var shell = Create(orchestrator: orchestrator, stateStore: store);
        var alpha = OpenNamedTab(shell, "alpha");
        var beta = OpenNamedTab(shell, "beta");
        beta.QueuedAfter = alpha;

        // Act
        alpha.StartWorkflowCommand.Execute(null);

        // Assert
        Assert.False(alpha.IsTaskFinished);
        Assert.False(beta.IsRunning);
        Assert.Same(alpha, beta.QueuedAfter);
        Assert.Equal(new[] { alpha }, beta.QueueCandidates);
        Assert.Equal(new[] { "alpha" }, orchestrator.Started);

        shell.ShutdownAll();
    }

    [StaFact]
    public void ClosingThePredecessor_CancelsTheQueueAndSaysSo()
    {
        // Arrange
        var shell = Create();
        var alpha = OpenNamedTab(shell, "alpha");
        var beta = OpenNamedTab(shell, "beta");
        EmulateSelector(beta);
        beta.QueuedAfter = alpha;

        // Act
        shell.CloseTabCommand.Execute(alpha);

        // Assert
        Assert.Null(beta.QueuedAfter);
        Assert.Equal("Queue aufgehoben: 'alpha' wurde geschlossen.", beta.InfoMessage);
        Assert.Empty(beta.QueueCandidates);

        shell.ShutdownAll();
    }

    [StaFact]
    public void AQueueChain_StartsOnlyTheDirectSuccessor()
    {
        // Arrange
        var store = new FakeTaskStateStore();
        var orchestrator = new QueueOrchestrator(store, PhaseStatus.Completed, "alpha");
        var shell = Create(orchestrator: orchestrator, stateStore: store);
        var alpha = OpenNamedTab(shell, "alpha");
        var beta = OpenNamedTab(shell, "beta");
        var gamma = OpenNamedTab(shell, "gamma");
        beta.QueuedAfter = alpha;
        gamma.QueuedAfter = beta;

        // Act
        alpha.StartWorkflowCommand.Execute(null);

        // Assert
        Assert.True(beta.IsRunning);
        Assert.False(gamma.IsRunning);
        Assert.Same(beta, gamma.QueuedAfter);
        Assert.Equal(new[] { "alpha", "beta" }, orchestrator.Started);

        shell.ShutdownAll();
    }

    [StaFact]
    public void TwoTabsQueuedOnOnePredecessor_InDifferentWorkingDirectories_BothStartInTabOrder()
    {
        // Arrange
        var store = new FakeTaskStateStore();
        var orchestrator = new QueueOrchestrator(store, PhaseStatus.Completed, "alpha");
        var shell = Create(orchestrator: orchestrator, stateStore: store);
        var alpha = OpenNamedTab(shell, "alpha");
        var beta = OpenNamedTab(shell, "beta");
        var gamma = OpenNamedTab(shell, "gamma");
        gamma.WorkingDirectory = Directory.CreateDirectory(Path.Combine(_root, "other")).FullName;
        gamma.QueuedAfter = alpha;
        beta.QueuedAfter = alpha;

        // Act
        alpha.StartWorkflowCommand.Execute(null);

        // Assert
        Assert.Equal(new[] { "alpha", "beta", "gamma" }, orchestrator.Started);

        shell.ShutdownAll();
    }

    [StaFact]
    public void TwoTabsQueuedOnOnePredecessor_InOneWorkingDirectory_StartOneAfterTheOther()
    {
        // Arrange
        var store = new FakeTaskStateStore();
        var orchestrator = new QueueOrchestrator(store, PhaseStatus.Completed, "alpha");
        var shell = Create(orchestrator: orchestrator, stateStore: store);
        var alpha = OpenNamedTab(shell, "alpha");
        var beta = OpenNamedTab(shell, "beta");
        var gamma = OpenNamedTab(shell, "gamma");
        EmulateSelector(gamma);
        beta.QueuedAfter = alpha;
        gamma.QueuedAfter = alpha;

        // Act
        alpha.StartWorkflowCommand.Execute(null);

        // Assert
        Assert.Equal(new[] { "alpha", "beta" }, orchestrator.Started);
        Assert.True(beta.IsRunning);
        Assert.False(gamma.IsRunning);
        Assert.Same(beta, gamma.QueuedAfter);
        Assert.Equal("Queue: 'alpha' ist fertig; wartet jetzt auf 'beta' (gleiches Arbeitsverzeichnis).", gamma.InfoMessage);
        Assert.DoesNotContain(alpha, beta.QueueCandidates);
        Assert.DoesNotContain(alpha, gamma.QueueCandidates);

        shell.ShutdownAll();
    }

    [StaFact]
    public void AQueuedStartWhoseDescriptionCannotBeSaved_IsRefusedAndTheNextSiblingStarts()
    {
        // Arrange
        var store = new FakeTaskStateStore { FailDescriptionWriteFor = "beta" };
        var orchestrator = new QueueOrchestrator(store, PhaseStatus.Completed, "alpha");
        var shell = Create(orchestrator: orchestrator, stateStore: store);
        var alpha = OpenNamedTab(shell, "alpha");
        var beta = OpenNamedTab(shell, "beta");
        var gamma = OpenNamedTab(shell, "gamma");
        beta.QueuedAfter = alpha;
        gamma.QueuedAfter = alpha;

        // Act
        alpha.StartWorkflowCommand.Execute(null);

        // Assert
        Assert.False(beta.IsRunning);
        Assert.Equal("Die Taskbeschreibung konnte nicht gespeichert werden: simulated description write failure", beta.ValidationMessage);
        Assert.Equal("Queue: 'alpha' ist fertig, der Workflow konnte aber nicht automatisch gestartet werden.", beta.InfoMessage);
        Assert.Null(beta.QueuedAfter);
        Assert.True(gamma.IsRunning);
        Assert.Equal(new[] { "alpha", "gamma" }, orchestrator.Started);
        Assert.DoesNotContain(alpha, beta.QueueCandidates);
        Assert.DoesNotContain(alpha, gamma.QueueCandidates);

        shell.ShutdownAll();
    }

    [StaFact]
    public void ANewTab_IsNotOfferedAFinishedTab()
    {
        // Arrange
        var store = new FakeTaskStateStore();
        var shell = Create(orchestrator: new QueueOrchestrator(store, PhaseStatus.Completed, "alpha"), stateStore: store);
        var alpha = OpenNamedTab(shell, "alpha");
        var beta = OpenNamedTab(shell, "beta");
        alpha.StartWorkflowCommand.Execute(null);

        // Act
        var gamma = OpenNamedTab(shell, "gamma");

        // Assert
        Assert.True(alpha.IsTaskFinished);
        Assert.Equal(new[] { beta }, gamma.QueueCandidates);
        Assert.Equal(new[] { gamma }, beta.QueueCandidates);

        shell.ShutdownAll();
    }

    [StaFact]
    public void ClosingAQueuedTab_RemovesItAndItsQueue()
    {
        // Arrange
        var store = new FakeTaskStateStore();
        var orchestrator = new QueueOrchestrator(store, PhaseStatus.Completed, "alpha");
        var shell = Create(orchestrator: orchestrator, stateStore: store);
        var alpha = OpenNamedTab(shell, "alpha");
        var beta = OpenNamedTab(shell, "beta");
        beta.QueuedAfter = alpha;
        shell.CloseTabCommand.Execute(beta);

        // Act
        alpha.StartWorkflowCommand.Execute(null);

        // Assert
        Assert.Equal(new[] { "alpha" }, orchestrator.Started);
        Assert.Empty(alpha.QueueCandidates);
        Assert.Single(shell.Tabs);

        shell.ShutdownAll();
    }
}
