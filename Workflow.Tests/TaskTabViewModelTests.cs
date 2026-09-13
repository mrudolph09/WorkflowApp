using System.ComponentModel;
using System.IO;
using System.Windows.Threading;
using Workflow.Models;
using Workflow.Services;
using Workflow.Terminal;
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
        ITaskFolderService? folders = null)
    {
        var terminal = new TerminalViewModel(
            new WebViewEnvironmentProvider(),
            new ConPtySessionFactory(),
            Dispatcher.CurrentDispatcher);

        return new TaskTabViewModel(
            folders ?? new TaskFolderService(),
            orchestrator ?? new StubOrchestrator(),
            _settings,
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

    /// <summary>A folder service whose rename always fails, as a locked or colliding one would.</summary>
    private sealed class FailingRenameFolderService : ITaskFolderService
    {
        private readonly TaskFolderService _inner = new();

        public TaskNameValidation Validate(string? taskName, string? workingDirectory) =>
            _inner.Validate(taskName, workingDirectory);

        public void EnsureCreated(TaskPaths paths) => _inner.EnsureCreated(paths);

        public bool DirectoryAlreadyExisted(TaskPaths paths) => _inner.DirectoryAlreadyExisted(paths);

        public void Rename(string workingDirectory, string oldName, string newName) =>
            throw new IOException("Der Ordner wird von einem anderen Prozess verwendet.");
    }

    private sealed class StubDirectoryPicker(string? result) : IDirectoryPickerService
    {
        public string? PickDirectory(string? initialDirectory) => result;
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
    public void Phases_AreTheFourStationsInOrderAndStartPending()
    {
        using var vm = Create();

        Assert.Equal(4, vm.Phases.Count);
        Assert.Equal("Spezifikation", vm.Phases[0].DisplayName);
        Assert.Equal("Review", vm.Phases[1].DisplayName);
        Assert.Equal("Review umsetzen", vm.Phases[2].DisplayName);
        Assert.Equal("Implementierung", vm.Phases[3].DisplayName);
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
    public void ApplyProgress_DrivesTheIndicatorColours()
    {
        using var vm = Create();

        vm.ApplyProgress(new PhaseProgress(WorkflowPhase.Specification, PhaseStatus.Active));
        Assert.Equal(PhaseStatus.Active, vm.Phases[0].Status);

        vm.ApplyProgress(new PhaseProgress(WorkflowPhase.Specification, PhaseStatus.Completed));
        vm.ApplyProgress(new PhaseProgress(WorkflowPhase.Review, PhaseStatus.Active));

        Assert.Equal(PhaseStatus.Completed, vm.Phases[0].Status);
        Assert.Equal(PhaseStatus.Active, vm.Phases[1].Status);
        Assert.Equal(PhaseStatus.Pending, vm.Phases[2].Status);
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
}
