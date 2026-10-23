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
/// The tab's open-gate-item state - count, read-error flag and copy text - always describes the task
/// the tab currently names: after a load, after typing a task name or switching the working
/// directory, after Implementation completes, and when the overview is opened.
/// </remarks>
public sealed class TaskTabOpenGateItemsTests : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private const string TaskName = "alpha";

    private readonly string _root;
    private readonly string _workspace;
    private readonly string _otherWorkspace;
    private readonly string _tracking;
    private readonly SettingsService _settings;

    public TaskTabOpenGateItemsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wf-tabgate-" + Guid.NewGuid().ToString("N"));
        _workspace = Path.Combine(_root, "arbeit");
        _otherWorkspace = Path.Combine(_root, "andere");
        _tracking = Path.Combine(_root, "workflows");
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_otherWorkspace);
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
    private TaskTabViewModel Create()
    {
        var terminal = new TerminalViewModel(
            new WebViewEnvironmentProvider(),
            new ConPtySessionFactory(),
            Dispatcher.CurrentDispatcher);

        return new TaskTabViewModel(
            new TaskFolderService(),
            new StubOrchestrator(),
            _settings,
            new FakeTaskStateStore(),
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

    private sealed class StubDirectoryPicker : IDirectoryPickerService
    {
        public string? PickDirectory(string? initialDirectory, string title = IDirectoryPickerService.DefaultTitle) => null;
    }

    private static TaskPaths TaskOnDisk(string workspace)
    {
        var paths = new TaskPaths(workspace, TaskName);
        Directory.CreateDirectory(paths.TaskDirectory);
        return paths;
    }

    private static void WriteDone(TaskPaths paths, string openSection) =>
        File.WriteAllText(paths.DoneAbsolute, "Umgesetzt.\n\n## Offene Punkte\n" + openSection, Utf8);

    /// <summary>A journal with every phase Completed: the task is finished.</summary>
    private static TaskState FinishedJournal()
    {
        var state = new TaskState
        {
            TaskDescription = "die Beschreibung",
            CreatedUtc = DateTimeOffset.UnixEpoch,
            UpdatedUtc = DateTimeOffset.UnixEpoch,
        };

        foreach (var definition in PhaseCatalog.All)
        {
            state.Phases.Add(new TaskPhaseState(definition.Phase, PhaseStatus.Completed, DateTimeOffset.UnixEpoch));
        }

        return state;
    }

    [StaFact]
    public void LoadForResume_DoneFileWithTwoOpenItems_ShowsThemUnderImplementierung()
    {
        // Arrange
        using var vm = Create();
        var paths = TaskOnDisk(_workspace);
        WriteDone(paths, "- V40: Popup prüfen\n- V41: Kopieren prüfen\n");

        // Act
        vm.LoadForResume(new RecoverableTask(paths, FinishedJournal(), null));

        // Assert
        Assert.True(vm.HasOpenGateItems);
        Assert.Equal(2, vm.OpenGateItemCount);
        Assert.False(vm.HasOpenGateReadErrors);
        Assert.Contains("Implementierung", vm.OpenGateItemsText, StringComparison.Ordinal);
        Assert.Contains("- V41: Kopieren prüfen", vm.OpenGateItemsText, StringComparison.Ordinal);
    }

    [StaFact]
    public void LoadForResume_DoneFileSaysKeine_ShowsNoIcon()
    {
        // Arrange
        using var vm = Create();
        var paths = TaskOnDisk(_workspace);
        WriteDone(paths, "- keine\n");

        // Act
        vm.LoadForResume(new RecoverableTask(paths, FinishedJournal(), null));

        // Assert
        Assert.False(vm.HasOpenGateItems);
        Assert.Equal(string.Empty, vm.OpenGateItemsText);
    }

    [StaFact]
    public void LoadForResume_SubtaskWithOpenItems_ListsThemUnderTheSubtaskTitle()
    {
        // Arrange
        using var vm = Create();
        var paths = TaskOnDisk(_workspace);
        var tracking = new SubtaskPaths(_tracking, TaskName);
        Directory.CreateDirectory(tracking.SubtaskDirectory("ST-001-lesen"));
        File.WriteAllText(tracking.ResultAbsolute, """{ "version": 1, "subtasks": ["ST-001-lesen"] }""", Utf8);
        File.WriteAllText(tracking.SubtaskMarkdown("ST-001-lesen"), "Beschreibung", Utf8);
        File.WriteAllText(
            tracking.SubtaskStatusFile("ST-001-lesen"),
            """{ "status": "complete", "openGateItems": ["V40: Kartenleser anschliessen"] }""",
            Utf8);
        var state = FinishedJournal();
        state.SubtasksEnabled = true;
        state.WorkflowDirectory = _tracking;

        // Act
        vm.LoadForResume(new RecoverableTask(paths, state, null));

        // Assert
        Assert.Equal(1, vm.OpenGateItemCount);
        Assert.Contains("ST-001-lesen", vm.OpenGateItemsText, StringComparison.Ordinal);
        Assert.Contains("- V40: Kartenleser anschliessen", vm.OpenGateItemsText, StringComparison.Ordinal);
    }

    [StaFact]
    public void TaskName_TypedNameOfAFinishedTaskWithItems_ShowsTheItems()
    {
        // Arrange
        using var vm = Create();
        WriteDone(TaskOnDisk(_workspace), "- V40: offen\n");
        vm.WorkingDirectory = _workspace;

        // Act
        vm.TaskName = TaskName;

        // Assert
        Assert.Equal(1, vm.OpenGateItemCount);
    }

    [StaFact]
    public void WorkingDirectory_SwitchedToAWorkspaceWithoutItemsThenNameCleared_FollowsTheCurrentTask()
    {
        // Arrange
        using var vm = Create();
        WriteDone(TaskOnDisk(_workspace), "- V40: offen\n");
        WriteDone(TaskOnDisk(_otherWorkspace), "- keine\n");
        vm.WorkingDirectory = _workspace;
        vm.TaskName = TaskName;
        var before = vm.OpenGateItemCount;

        // Act
        vm.WorkingDirectory = _otherWorkspace;
        var afterSwitch = vm.OpenGateItemCount;
        vm.TaskName = string.Empty;

        // Assert
        Assert.Equal(1, before);
        Assert.Equal(0, afterSwitch);
        Assert.Equal(0, vm.OpenGateItemCount);
        Assert.Equal(string.Empty, vm.OpenGateItemsText);
    }

    [StaFact]
    public void ApplyProgress_ImplementationCompleted_ReadsTheNewDoneFile()
    {
        // Arrange
        using var vm = Create();
        var paths = TaskOnDisk(_workspace);
        vm.WorkingDirectory = _workspace;
        vm.TaskName = TaskName;
        WriteDone(paths, "- V40: offen\n");

        // Act
        vm.ApplyProgress(new PhaseProgress(WorkflowPhase.Implementation, PhaseStatus.Completed));

        // Assert
        Assert.Equal(1, vm.OpenGateItemCount);
    }

    [StaFact]
    public void RefreshOpenGateItemsOnOpenCommand_DoneFileChangedWhileOpen_ShowsTheCurrentItems()
    {
        // Arrange
        using var vm = Create();
        var paths = TaskOnDisk(_workspace);
        WriteDone(paths, "- V40: offen\n");
        vm.LoadForResume(new RecoverableTask(paths, FinishedJournal(), null));
        WriteDone(paths, "- V40: offen\n- V41: neu\n");

        // Act
        vm.RefreshOpenGateItemsOnOpenCommand.Execute(null);

        // Assert
        Assert.Equal(2, vm.OpenGateItemCount);
        Assert.Contains("V41: neu", vm.OpenGateItemsText, StringComparison.Ordinal);
    }

    [StaFact]
    public void LoadForResume_DoneFileWithoutSection_ShowsTheReadErrorIcon()
    {
        // Arrange
        using var vm = Create();
        var paths = TaskOnDisk(_workspace);
        File.WriteAllText(paths.DoneAbsolute, "Umgesetzt; verify.ps1 exit 0.", Utf8);

        // Act
        vm.LoadForResume(new RecoverableTask(paths, FinishedJournal(), null));

        // Assert
        Assert.True(vm.HasOpenGateItems);
        Assert.True(vm.HasOpenGateReadErrors);
    }
}
