using System.IO;
using System.Windows.Threading;
using Workflow.Models;
using Workflow.Services;
using Workflow.Terminal;
using Workflow.Tests.Fakes;
using Workflow.ViewModels;

namespace Workflow.Tests;

/// <remarks>WF_DESKTOPJOBBUTTON spec sections 4.6 and 4.7.</remarks>
public sealed class TaskTabDesktopJobStartTests : IDisposable
{
    private readonly string _root;
    private readonly SettingsService _settings;

    public TaskTabDesktopJobStartTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wf-djstart-" + Guid.NewGuid().ToString("N"));
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

    private sealed class StubDirectoryPicker : IDirectoryPickerService
    {
        public string? PickDirectory(string? initialDirectory, string title = IDirectoryPickerService.DefaultTitle) => null;
    }

    private void MakeStartable(TaskTabViewModel vm)
    {
        vm.WorkingDirectory = _root;
        vm.TaskName = "demo";
    }

    private (TaskTabViewModel Vm, DesktopJobStartSignal Start) StartedTab(CapturingOrchestrator orchestrator)
    {
        var vm = Create(orchestrator);
        MakeStartable(vm);
        vm.StartWorkflowCommand.Execute(null);
        return (vm, orchestrator.Request!.DesktopJobStart!);
    }

    [Fact]
    public void ANewTab_CannotStartADesktopJob()
    {
        // Arrange
        using var vm = Create(new CapturingOrchestrator());

        // Act
        var canStart = vm.StartDesktopJobNowCommand.CanExecute(null);

        // Assert
        Assert.False(canStart);
        Assert.False(vm.IsDesktopJobWaiting);
    }

    [Fact]
    public void ARunWaitingForIdle_EnablesTheCommandUntilTheWaitEnds()
    {
        // Arrange
        var (vm, start) = StartedTab(new CapturingOrchestrator());
        using var tab = vm;

        // Act
        _ = start.BeginWait();
        var whileWaiting = vm.StartDesktopJobNowCommand.CanExecute(null);
        start.EndWait();

        // Assert
        Assert.True(whileWaiting);
        Assert.False(vm.StartDesktopJobNowCommand.CanExecute(null));
        Assert.False(vm.IsDesktopJobWaiting);
    }

    [Fact]
    public void APausedTab_CannotStartADesktopJob()
    {
        // Arrange
        var (vm, start) = StartedTab(new CapturingOrchestrator());
        using var tab = vm;
        _ = start.BeginWait();

        // Act
        vm.PauseCommand.Execute(null);
        var whilePaused = vm.StartDesktopJobNowCommand.CanExecute(null);
        vm.UnpauseCommand.Execute(null);

        // Assert
        Assert.False(whilePaused);
        Assert.True(vm.StartDesktopJobNowCommand.CanExecute(null));
    }

    [Fact]
    public async Task StartDesktopJobNow_CompletesTheRunsWait()
    {
        // Arrange
        var (vm, start) = StartedTab(new CapturingOrchestrator());
        using var tab = vm;
        var wait = start.BeginWait();

        // Act
        vm.StartDesktopJobNowCommand.Execute(null);

        // Assert
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(wait.IsCompletedSuccessfully);
    }
}
