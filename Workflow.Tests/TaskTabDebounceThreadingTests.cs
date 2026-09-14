using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using Workflow.Models;
using Workflow.Services;
using Workflow.Terminal;
using Workflow.ViewModels;

namespace Workflow.Tests;

/// <summary>
/// Reproduces the production path: a NON-ZERO folder debounce, which pushes SyncFolder onto a
/// thread-pool thread. Every other TaskTabViewModel test uses TimeSpan.Zero and therefore only
/// ever exercises the synchronous branch.
/// </summary>
public sealed class TaskTabDebounceThreadingTests : IDisposable
{
    private readonly string _root;
    private readonly SettingsService _settings;

    public TaskTabDebounceThreadingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wf-dbnc-" + Guid.NewGuid().ToString("N"));
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

    private sealed class StubOrchestrator : IWorkflowOrchestrator
    {
        public Task RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.Infinite, cancellationToken);
    }

    private sealed class StubDirectoryPicker : IDirectoryPickerService
    {
        public string? PickDirectory(string? initialDirectory) => null;
    }

#pragma warning disable CA2000
    private TaskTabViewModel Create() => new(
        new TaskFolderService(),
        new StubOrchestrator(),
        _settings,
        new StubDirectoryPicker(),
        new TerminalViewModel(
            new WebViewEnvironmentProvider(),
            new ConPtySessionFactory(),
            Dispatcher.CurrentDispatcher),
        folderDebounce: TimeSpan.FromMilliseconds(50),
        []);
#pragma warning restore CA2000

    [WpfFact]
    public async Task Start_BecomesClickable_AfterTheDirectoryIsChosen()
    {
        // Regression: the debounced sync used to run on a thread-pool thread via Task.Run. The
        // ValidationMessage setter then raised StartWorkflowCommand.CanExecuteChanged off the UI
        // thread, WPF's ButtonBase threw a cross-thread InvalidOperationException into the
        // un-awaited task, and the Button stayed disabled forever while CanExecute(null) was true.
        using var vm = Create();
        var button = new Button { Command = vm.StartWorkflowCommand };

        // 1. Name typed while no directory is selected: the debounced sync writes the error.
        vm.TaskName = "demo";
        await Task.Delay(400);

        // 2. Directory chosen: the next debounced sync must clear the error and create the folder.
        vm.WorkingDirectory = _root;
        await Task.Delay(400);

        Assert.Null(vm.ValidationMessage);
        Assert.True(vm.StartWorkflowCommand.CanExecute(null), "command says it cannot execute");
        Assert.True(button.IsEnabled, "the Button never re-evaluated CanExecute");
        Assert.True(Directory.Exists(Path.Combine(_root, "demo")), "task folder was not created");
    }
}
