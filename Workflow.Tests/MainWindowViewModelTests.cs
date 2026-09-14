using System.IO;
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

    private sealed class StubFactory(SettingsService settings, IReadOnlyList<string> startupErrors)
        : ITaskTabViewModelFactory
    {
        // CA2000 cannot trace either disposal: WebViewEnvironmentProvider is a deliberate
        // app-wide singleton never disposed per-tab in production either, and TerminalViewModel's
        // ownership passes into the returned TaskTabViewModel, whose own Dispose() disposes it.
#pragma warning disable CA2000
        public TaskTabViewModel Create() => new(
            new TaskFolderService(),
            new StubOrchestrator(),
            settings,
            new FakeTaskStateStore(),
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
            public string? PickDirectory(string? initialDirectory) => null;
        }
    }

    private MainWindowViewModel Create(IReadOnlyList<string>? startupErrors = null) =>
        new(new StubFactory(_settings, startupErrors ?? []), new StubScanner(), startupErrors ?? []);

    // --- Regressions pinned by the review -------------------------------------------------

    [StaFact]
    public void StartupErrors_DisableStartOnTheInitialTab()
    {
        var vm = Create(["review_prompt.md ist leer."]);

        Assert.True(vm.HasStartupErrors);
        Assert.False(vm.Tabs[0].StartWorkflowCommand.CanExecute(null));
    }

    [StaFact]
    public void StartupErrors_DisableStartOnTabsOpenedAfterTheDialogWasDismissed()
    {
        // F17: the modal is dismissible and '+' keeps working, so the gate must travel with the
        // factory rather than living only on this view model.
        var vm = Create(["review_prompt.md ist leer."]);

        vm.AddTaskTabCommand.Execute(null);

        Assert.Equal(2, vm.Tabs.Count);
        Assert.All(vm.Tabs, tab => Assert.False(tab.StartWorkflowCommand.CanExecute(null)));
    }

    [StaFact]
    public void Startup_OpensExactlyOneTabAndSelectsIt()
    {
        var vm = Create();

        Assert.Single(vm.Tabs);
        Assert.Same(vm.Tabs[0], vm.SelectedTab);
        Assert.Equal("(Bezeichnung)", vm.Tabs[0].Header);
    }

    [StaFact]
    public void AddTaskTab_AppendsAndSelectsTheNewTab()
    {
        var vm = Create();

        vm.AddTaskTabCommand.Execute(null);

        Assert.Equal(2, vm.Tabs.Count);
        Assert.Same(vm.Tabs[1], vm.SelectedTab);
    }

    [StaFact]
    public void CloseTab_RemovesAndDisposesIt()
    {
        var vm = Create();
        vm.AddTaskTabCommand.Execute(null);
        var first = vm.Tabs[0];

        vm.CloseTabCommand.Execute(first);

        Assert.Single(vm.Tabs);
        Assert.DoesNotContain(first, vm.Tabs);
    }

    [StaFact]
    public void ClosingTheLastTab_OpensAFreshOne()
    {
        var vm = Create();

        vm.CloseTabCommand.Execute(vm.Tabs[0]);

        Assert.Single(vm.Tabs);
        Assert.Equal("(Bezeichnung)", vm.Tabs[0].Header);
    }

    [StaFact]
    public void ATabsCloseRequest_ClosesIt()
    {
        var vm = Create();
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
        var vm = Create();
        vm.AddTaskTabCommand.Execute(null);

        vm.ShutdownAll();

        Assert.Empty(vm.Tabs);
    }

    private sealed class StubScanner(params RecoverableTask[] tasks) : ITaskRecoveryScanner
    {
        public Task<IReadOnlyList<RecoverableTask>> ScanAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RecoverableTask>>(tasks);
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

    [Fact]
    public async Task InitialiseAsync_AppendsOneTabPerRecoveredTaskAndSelectsTheFirst()
    {
        var shell = new MainWindowViewModel(
            new StubFactory(_settings, []),
            new StubScanner(MakeRecoverable("alpha"), MakeRecoverable("beta")),
            []);

        await shell.InitialiseAsync();

        Assert.Equal(3, shell.Tabs.Count);          // the blank tab plus two recovered ones
        Assert.Equal("alpha", shell.Tabs[1].TaskName);
        Assert.Equal("beta", shell.Tabs[2].TaskName);
        Assert.Same(shell.Tabs[1], shell.SelectedTab);

        shell.ShutdownAll();
    }

    [Fact]
    public async Task InitialiseAsync_NothingToRecover_LeavesTheBlankTabSelected()
    {
        var shell = new MainWindowViewModel(new StubFactory(_settings, []), new StubScanner(), []);

        await shell.InitialiseAsync();

        Assert.Single(shell.Tabs);
        Assert.Same(shell.Tabs[0], shell.SelectedTab);

        shell.ShutdownAll();
    }
}
