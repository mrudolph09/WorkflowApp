using System.IO;
using System.Windows.Threading;
using Workflow.Models;
using Workflow.Services;
using Workflow.Terminal;
using Workflow.Tests.Fakes;
using Workflow.ViewModels;

namespace Workflow.Tests;

/// <remarks>Workflow_LOAD_AND_PAUSE spec sections 6.4 and 6.6: the tab's Pause/Play surface.</remarks>
public sealed class TaskTabPauseTests : IDisposable
{
    private readonly string _root;
    private readonly SettingsService _settings;

    public TaskTabPauseTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wf-pause-" + Guid.NewGuid().ToString("N"));
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

    // CA2000 cannot trace either disposal: WebViewEnvironmentProvider is a deliberate app-wide
    // singleton never disposed per tab in production either, and TerminalViewModel's ownership
    // passes into the returned tab, whose Dispose() disposes it - every call site holds the tab in
    // a `using`.
#pragma warning disable CA2000
    private TaskTabViewModel Create(IWorkflowOrchestrator orchestrator) => new(
        new TaskFolderService(),
        orchestrator,
        _settings,
        new FakeTaskStateStore(),
        new StubDirectoryPicker(),
        new TerminalViewModel(
            new WebViewEnvironmentProvider(),
            new ConPtySessionFactory(),
            Dispatcher.CurrentDispatcher),
        folderDebounce: TimeSpan.Zero,
        startupErrors: []);
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

    /// <summary>A run that ends at once, as one whose last phase has just completed.</summary>
    private sealed class FinishingOrchestrator : IWorkflowOrchestrator
    {
        public WorkflowRunRequest? Request { get; private set; }

        public Task RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.CompletedTask;
        }
    }

    private sealed class StubDirectoryPicker : IDirectoryPickerService
    {
        public string? PickDirectory(string? initialDirectory, string title = IDirectoryPickerService.DefaultTitle) => null;
    }

    private void MakeStartable(TaskTabViewModel vm)
    {
        vm.WorkingDirectory = _root;
        vm.TaskName = "demo";
    }

    [Fact]
    public void ANewTab_IsNotPausedAndOffersPauseButNotPlay()
    {
        // Arrange
        using var vm = Create(new CapturingOrchestrator());

        // Act
        var canPause = vm.PauseCommand.CanExecute(null);
        var canPlay = vm.UnpauseCommand.CanExecute(null);

        // Assert
        Assert.False(vm.IsPaused);
        Assert.True(canPause);
        Assert.False(canPlay);
    }

    [Fact]
    public void Pause_SetsIsPausedAndOffersPlayInsteadOfPause()
    {
        // Arrange
        using var vm = Create(new CapturingOrchestrator());
        var announced = new List<string?>();
        vm.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        // Act
        vm.PauseCommand.Execute(null);

        // Assert - the Play button's Visibility binding depends on this notification.
        Assert.True(vm.IsPaused);
        Assert.Contains(nameof(TaskTabViewModel.IsPaused), announced);
        Assert.False(vm.PauseCommand.CanExecute(null));
        Assert.True(vm.UnpauseCommand.CanExecute(null));
    }

    [Fact]
    public void Unpause_ClearsIsPausedAndOffersPauseAgain()
    {
        // Arrange
        using var vm = Create(new CapturingOrchestrator());
        vm.PauseCommand.Execute(null);

        // Act
        vm.UnpauseCommand.Execute(null);

        // Assert
        Assert.False(vm.IsPaused);
        Assert.True(vm.PauseCommand.CanExecute(null));
        Assert.False(vm.UnpauseCommand.CanExecute(null));
    }

    [Fact]
    public void StartWorkflow_HandsTheRunTheTabsOwnPauseGate()
    {
        // Arrange
        var orchestrator = new CapturingOrchestrator();
        using var vm = Create(orchestrator);
        MakeStartable(vm);

        // Act
        vm.StartWorkflowCommand.Execute(null);

        // Assert - the gate the run holds follows the buttons in both directions.
        Assert.NotNull(orchestrator.Request);
        var gate = orchestrator.Request.Pause;
        Assert.NotNull(gate);
        Assert.False(gate.IsPaused);
        vm.PauseCommand.Execute(null);
        Assert.True(gate.IsPaused);
        vm.UnpauseCommand.Execute(null);
        Assert.False(gate.IsPaused);
    }

    [Fact]
    public void ARunThatPausesTheGate_OffersPlay()
    {
        // Arrange - the decision pre-flight (B7) holds a run for approval by pausing the gate from
        // code; the user approves with Play, so the button has to appear.
        var orchestrator = new CapturingOrchestrator();
        using var vm = Create(orchestrator);
        MakeStartable(vm);
        vm.StartWorkflowCommand.Execute(null);
        Assert.NotNull(orchestrator.Request);
        Assert.NotNull(orchestrator.Request.Pause);

        // Act
        orchestrator.Request.Pause.Pause();

        // Assert
        Assert.True(vm.IsPaused);
        Assert.True(vm.UnpauseCommand.CanExecute(null));

        vm.UnpauseCommand.Execute(null);
        Assert.False(orchestrator.Request.Pause.IsPaused);
    }

    [Fact]
    public void APauseArmedBeforeStart_ReachesTheRun()
    {
        // Arrange
        var orchestrator = new CapturingOrchestrator();
        using var vm = Create(orchestrator);
        MakeStartable(vm);
        vm.PauseCommand.Execute(null);

        // Act
        vm.StartWorkflowCommand.Execute(null);

        // Assert
        Assert.NotNull(orchestrator.Request);
        Assert.NotNull(orchestrator.Request.Pause);
        Assert.True(orchestrator.Request.Pause.IsPaused);
    }

    [Fact]
    public void Pause_StaysAvailableWhileAPhaseRunsAndNeverRaisesTheManualSignal()
    {
        // Arrange
        var orchestrator = new CapturingOrchestrator();
        using var vm = Create(orchestrator);
        MakeStartable(vm);
        vm.StartWorkflowCommand.Execute(null);
        vm.ApplyProgress(new PhaseProgress(WorkflowPhase.Implementation, PhaseStatus.Active));

        // Act
        var canPause = vm.PauseCommand.CanExecute(null);
        vm.PauseCommand.Execute(null);
        vm.UnpauseCommand.Execute(null);

        // Assert - 'Task abschliessen' stays the only writer of the manual signal.
        Assert.True(vm.IsRunning);
        Assert.True(canPause);
        Assert.NotNull(orchestrator.Request);
        Assert.False(orchestrator.Request.ManualSignal.WaitAsync(CancellationToken.None).IsCompleted);
    }

    [Fact]
    public async Task APause_SurvivesTheEndOfARun()
    {
        // Arrange - Review Focus 3: a pause pressed during phase 4 has nothing to hold in that run
        // and must still be armed for the next one.
        var orchestrator = new FinishingOrchestrator();
        using var vm = Create(orchestrator);
        MakeStartable(vm);
        vm.PauseCommand.Execute(null);

        // Act
        vm.StartWorkflowCommand.Execute(null);
        await Task.Yield();

        // Assert
        Assert.False(vm.IsRunning);
        Assert.True(vm.IsPaused);
        Assert.NotNull(orchestrator.Request);
        Assert.NotNull(orchestrator.Request.Pause);
        Assert.True(orchestrator.Request.Pause.IsPaused);
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

    [Fact]
    public async Task Dispose_WhileTheRunIsHeld_EndsItByCancellationWithoutAnError()
    {
        // Arrange
        var orchestrator = new HeldRunOrchestrator();
        var vm = Create(orchestrator);
        MakeStartable(vm);
        vm.PauseCommand.Execute(null);
        vm.StartWorkflowCommand.Execute(null);
        Assert.NotNull(orchestrator.RunTask);
        Assert.False(orchestrator.RunTask.IsCompleted);
        Assert.True(vm.IsRunning);

        // Act
        vm.Dispose();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await orchestrator.RunTask.WaitAsync(TimeSpan.FromSeconds(10)));
        await WaitUntilAsync(() => !vm.IsRunning);
        Assert.False(vm.IsRunning);
        Assert.Null(vm.ValidationMessage);
    }
}
