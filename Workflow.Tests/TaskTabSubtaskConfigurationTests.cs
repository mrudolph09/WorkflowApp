using System.IO;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using Workflow.Models;
using Workflow.Services;
using Workflow.Terminal;
using Workflow.Tests.Fakes;
using Workflow.ViewModels;

namespace Workflow.Tests;

/// <remarks>
/// <para>
/// Task 6.3: the tab owns the subtask configuration bindings - the tracking-directory MRU
/// (requirement 1.4), the blocking German message and the start gate (requirement 1.3), and the
/// phase-4 editing lock over every subtask input including the folder-picker button
/// (requirement 1.6, design "Resolved Decisions" issue 3).
/// </para>
/// <para>
/// Every fixture here is deliberately asymmetric: the tracking directory, the working directory
/// and the second tracking directory are three distinct paths, and the two MRU lists are asserted
/// against each other, so a mutant that writes the tracking choice into the working-directory MRU -
/// or reads the wrong list back - fails instead of passing on a symmetric fixture.
/// </para>
/// </remarks>
public sealed class TaskTabSubtaskConfigurationTests : IDisposable
{
    private const string TaskName = "alpha";

    private readonly string _root;
    private readonly string _workspace;
    private readonly string _tracking;
    private readonly string _otherTracking;
    private readonly string _settingsPath;
    private readonly SettingsService _settings;

    public TaskTabSubtaskConfigurationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wf-tab63-" + Guid.NewGuid().ToString("N"));
        _workspace = Path.Combine(_root, "arbeit");
        _tracking = Path.Combine(_root, "workflows");
        _otherTracking = Path.Combine(_root, "workflows-zwei");

        Directory.CreateDirectory(_workspace);
        MakeTrackingRepository(_tracking);
        MakeTrackingRepository(_otherTracking);

        _settingsPath = Path.Combine(_root, "settings.json");
        _settings = new SettingsService(_settingsPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void MakeTrackingRepository(string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, SubtaskPaths.TemplateFolderName));
    }

    // CA2000 cannot trace the disposal: TerminalViewModel's ownership passes into the returned
    // TaskTabViewModel, whose own Dispose() disposes it, and every call site uses `using var`.
#pragma warning disable CA2000
    private TaskTabViewModel Create(
        ISettingsService? settings = null,
        IWorkflowOrchestrator? orchestrator = null,
        IDirectoryPickerService? picker = null)
    {
        var terminal = new TerminalViewModel(
            new WebViewEnvironmentProvider(),
            new ConPtySessionFactory(),
            Dispatcher.CurrentDispatcher);

        return new TaskTabViewModel(
            new TaskFolderService(),
            orchestrator ?? new StubOrchestrator(),
            settings ?? _settings,
            new FakeTaskStateStore(),
            picker ?? new StubDirectoryPicker(),
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

    private sealed class RecordingDirectoryPicker(string? result) : IDirectoryPickerService
    {
        public string? InitialDirectory { get; private set; }

        public string? Title { get; private set; }

        public string? PickDirectory(string? initialDirectory, string title = IDirectoryPickerService.DefaultTitle)
        {
            InitialDirectory = initialDirectory;
            Title = title;
            return result;
        }
    }

    private TaskState Journal()
    {
        var paths = new TaskPaths(_workspace, TaskName);
        Directory.CreateDirectory(paths.TaskDirectory);

        return new TaskState { TaskDescription = "beschreibung" };
    }

    // -----------------------------------------------------------------------------------------
    // Requirement 1.4: a separate history, preselected on a new tab
    // -----------------------------------------------------------------------------------------

    [StaFact]
    public void ANewTab_PreselectsTheLastTrackingDirectoryAndOffersTheRecordedHistory()
    {
        _settings.AddRecentWorkflowDirectory(_otherTracking);
        _settings.AddRecentWorkflowDirectory(_tracking);
        _settings.AddRecentDirectory(_workspace);

        using var vm = Create();

        Assert.Equal(_tracking, vm.WorkflowDirectory);
        Assert.Equal(new[] { _tracking, _otherTracking }, vm.RecentWorkflowDirectories);

        // Asymmetry: the two histories are different lists and must not be crossed.
        Assert.Equal(new[] { _workspace }, vm.RecentDirectories);
        Assert.DoesNotContain(_workspace, vm.RecentWorkflowDirectories);
    }

    [StaFact]
    public void SelectingATrackingDirectory_PersistsItAsTheLastChoiceAndInItsOwnHistory()
    {
        using var vm = Create();
        vm.WorkingDirectory = _workspace;

        vm.WorkflowDirectory = _otherTracking;
        vm.WorkflowDirectory = _tracking;

        Assert.Equal(_tracking, _settings.Settings.LastWorkflowDirectory);
        Assert.Equal(new[] { _tracking, _otherTracking }, _settings.Settings.RecentWorkflowDirectories);
        Assert.Equal(new[] { _tracking, _otherTracking }, vm.RecentWorkflowDirectories);

        // The working-directory history saw only the working directory.
        Assert.Equal(_workspace, _settings.Settings.LastDirectory);
        Assert.Equal(new[] { _workspace }, _settings.Settings.RecentDirectories);

        // Persisted, not merely in memory.
        Assert.Contains(
            _tracking.Replace(@"\", @"\\", StringComparison.Ordinal),
            File.ReadAllText(_settingsPath),
            StringComparison.Ordinal);
    }

    [StaFact]
    public void AnInvalidTrackingDirectory_IsNotRecordedInTheHistory()
    {
        // Requirement 1.4 promises the history for a VALID choice only; a directory without the
        // marker must not be offered again on the next tab.
        var notTracking = Path.Combine(_root, "kein-repo");
        Directory.CreateDirectory(notTracking);

        using var vm = Create();
        vm.WorkflowDirectory = notTracking;

        Assert.Empty(_settings.Settings.RecentWorkflowDirectories);
        Assert.Empty(vm.RecentWorkflowDirectories);
        Assert.Null(_settings.Settings.LastWorkflowDirectory);
    }

    [StaFact]
    public void ANullRecentWorkflowDirectoriesList_DoesNotCrashTheTab()
    {
        // System.Text.Json assigns null straight over the property initializer, so a settings file
        // carrying an explicit null reaches the tab as a null collection.
        var path = Path.Combine(_root, "nulled.json");
        File.WriteAllText(path, """{ "RecentWorkflowDirectories": null, "RecentDirectories": [] }""");

        var settings = new SettingsService(path);
        Assert.Null(settings.Settings.RecentWorkflowDirectories);

        using var vm = Create(settings: settings);

        Assert.Empty(vm.RecentWorkflowDirectories);

        vm.WorkflowDirectory = _tracking;
        Assert.Equal(new[] { _tracking }, vm.RecentWorkflowDirectories);
    }

    // -----------------------------------------------------------------------------------------
    // The never-clear collection reconciliation, at ComboBox level
    // -----------------------------------------------------------------------------------------

#pragma warning disable CA2000
    [WpfFact]
    public void SelectingASecondTrackingDirectory_KeepsTheSelection()
    {
        using var vm = Create();

        // Exactly what TaskTabView.xaml does for the Workflow-Verzeichnis row.
        var combo = new ComboBox { DataContext = vm };
        combo.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.RecentWorkflowDirectories)));
        combo.SetBinding(
            Selector.SelectedItemProperty,
            new Binding(nameof(vm.WorkflowDirectory)) { Mode = BindingMode.TwoWay });

        vm.WorkflowDirectory = _otherTracking;
        Assert.Equal(_otherTracking, vm.WorkflowDirectory);

        vm.WorkflowDirectory = _tracking;

        Assert.Equal(_tracking, vm.WorkflowDirectory);
        Assert.Equal(_tracking, combo.SelectedItem);
        Assert.Equal(new[] { _tracking, _otherTracking }, vm.RecentWorkflowDirectories);
    }

    [WpfFact]
    public void ATrailingSeparatorOnTheTrackingDirectory_DoesNotBreakTheSelection()
    {
        using var vm = Create();

        var combo = new ComboBox { DataContext = vm };
        combo.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.RecentWorkflowDirectories)));
        combo.SetBinding(
            Selector.SelectedItemProperty,
            new Binding(nameof(vm.WorkflowDirectory)) { Mode = BindingMode.TwoWay });

        vm.WorkflowDirectory = _tracking + Path.DirectorySeparatorChar;

        Assert.Equal(_tracking, vm.WorkflowDirectory);
        Assert.Equal(_tracking, combo.SelectedItem);
    }
#pragma warning restore CA2000

    // -----------------------------------------------------------------------------------------
    // Requirement 1.3: the German message and the closed start gate
    // -----------------------------------------------------------------------------------------

    [StaFact]
    public void SubtaskModeWithoutATrackingDirectory_ShowsTheGermanMessageAndClosesTheStartGate()
    {
        using var vm = Create();
        vm.WorkingDirectory = _workspace;
        vm.TaskName = TaskName;

        Assert.True(vm.StartWorkflowCommand.CanExecute(null));

        vm.SubtasksEnabled = true;

        Assert.Equal("Bitte ein Workflow-Verzeichnis auswählen.", vm.WorkflowDirectoryMessage);
        Assert.False(vm.StartWorkflowCommand.CanExecute(null));

        // The working-directory channel is untouched: the two messages are separate surfaces.
        Assert.Null(vm.ValidationMessage);
    }

    [StaFact]
    public void ADirectoryWithoutTheMarker_ShowsTheGermanMessageAndClosesTheStartGate()
    {
        var notTracking = Path.Combine(_root, "kein-repo");
        Directory.CreateDirectory(notTracking);

        using var vm = Create();
        vm.WorkingDirectory = _workspace;
        vm.TaskName = TaskName;
        vm.SubtasksEnabled = true;
        vm.WorkflowDirectory = notTracking;

        Assert.NotNull(vm.WorkflowDirectoryMessage);
        Assert.Contains(SubtaskPaths.TemplateFolderName, vm.WorkflowDirectoryMessage, StringComparison.Ordinal);
        Assert.False(vm.StartWorkflowCommand.CanExecute(null));

        vm.WorkflowDirectory = _tracking;

        Assert.Null(vm.WorkflowDirectoryMessage);
        Assert.True(vm.StartWorkflowCommand.CanExecute(null));
    }

    [StaFact]
    public void DeselectingSubtaskMode_ClearsTheMessageAndReopensTheStartGate()
    {
        // Requirement 1.3 gates a workflow started "with subtask mode enabled"; the same invalid
        // directory must not keep a normal run hostage.
        using var vm = Create();
        vm.WorkingDirectory = _workspace;
        vm.TaskName = TaskName;
        vm.SubtasksEnabled = true;

        Assert.False(vm.StartWorkflowCommand.CanExecute(null));

        vm.SubtasksEnabled = false;

        Assert.Null(vm.WorkflowDirectoryMessage);
        Assert.True(vm.StartWorkflowCommand.CanExecute(null));
    }

    [StaFact]
    public void ClearingTheMarkerFromTheSelectedDirectory_ShowsTheMessageAndRefusesToStart()
    {
        // The observable of task 6.3: the directory was valid when it was picked and the marker
        // disappears afterwards, so no property change announces it. Invoking the start action has
        // to re-validate rather than trust the stale verdict.
        using var vm = Create();
        vm.WorkingDirectory = _workspace;
        vm.TaskName = TaskName;
        vm.SubtasksEnabled = true;
        vm.WorkflowDirectory = _tracking;

        Assert.Null(vm.WorkflowDirectoryMessage);
        Assert.True(vm.StartWorkflowCommand.CanExecute(null));

        Directory.Delete(Path.Combine(_tracking, SubtaskPaths.TemplateFolderName), recursive: true);

        vm.StartWorkflowCommand.Execute(null);

        Assert.NotNull(vm.WorkflowDirectoryMessage);
        Assert.Contains(SubtaskPaths.TemplateFolderName, vm.WorkflowDirectoryMessage, StringComparison.Ordinal);
        Assert.False(vm.IsRunning);
        Assert.False(vm.StartWorkflowCommand.CanExecute(null));
    }

    [StaFact]
    public void AStartupTemplateError_StillOwnsTheWorkingDirectoryMessage()
    {
        // The existing _startupErrors precedence in SyncFolder must not be clobbered by the new
        // subtask message: they are different channels and the startup error keeps its own.
#pragma warning disable CA2000
        using var terminal = new TerminalViewModel(
            new WebViewEnvironmentProvider(),
            new ConPtySessionFactory(),
            Dispatcher.CurrentDispatcher);
#pragma warning restore CA2000

        using var vm = new TaskTabViewModel(
            new TaskFolderService(),
            new StubOrchestrator(),
            _settings,
            new FakeTaskStateStore(),
            new StubDirectoryPicker(),
            terminal,
            folderDebounce: TimeSpan.Zero,
            startupErrors: ["Vorlage kaputt"]);

        vm.WorkingDirectory = _workspace;
        vm.TaskName = TaskName;
        vm.SubtasksEnabled = true;
        vm.WorkflowDirectory = _tracking;

        Assert.Equal("Vorlage kaputt", vm.ValidationMessage);
        Assert.Null(vm.WorkflowDirectoryMessage);
        Assert.False(vm.StartWorkflowCommand.CanExecute(null));
    }

    // -----------------------------------------------------------------------------------------
    // Requirement 1.6: the phase-4 editing lock covers every subtask input
    // -----------------------------------------------------------------------------------------

    [StaFact]
    public void WhilePhasesOneToThreeAreActive_TheSubtaskConfigurationStaysEditable()
    {
        using var vm = Create();

        foreach (var phase in new[] { WorkflowPhase.Specification, WorkflowPhase.Review, WorkflowPhase.ResolveReview })
        {
            vm.ApplyProgress(new PhaseProgress(phase, PhaseStatus.Active));

            Assert.True(vm.IsSubtaskConfigurationEditable);
            Assert.True(vm.BrowseWorkflowDirectoryCommand.CanExecute(null));
        }
    }

    [StaFact]
    public void WhilePhaseFourIsActive_EverySubtaskInputIsLocked()
    {
        using var vm = Create();

        var notifications = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TaskTabViewModel.IsSubtaskConfigurationEditable))
            {
                notifications++;
            }
        };

        vm.ApplyProgress(new PhaseProgress(WorkflowPhase.Implementation, PhaseStatus.Active));

        Assert.False(vm.IsSubtaskConfigurationEditable);
        Assert.False(vm.BrowseWorkflowDirectoryCommand.CanExecute(null));

        // Without the notification the XAML IsEnabled bindings never update, so the lock exists
        // only in the view model.
        Assert.True(notifications > 0, "ApplyProgress must raise PropertyChanged for IsSubtaskConfigurationEditable.");

        vm.ApplyProgress(new PhaseProgress(WorkflowPhase.Implementation, PhaseStatus.Completed));

        Assert.True(vm.IsSubtaskConfigurationEditable);
        Assert.True(vm.BrowseWorkflowDirectoryCommand.CanExecute(null));
    }

    // -----------------------------------------------------------------------------------------
    // The folder picker (task 6.2's title parameter)
    // -----------------------------------------------------------------------------------------

    [StaFact]
    public void BrowseWorkflowDirectory_AsksWithTheTrackingCaptionAndStoresTheChoice()
    {
        var picker = new RecordingDirectoryPicker(_tracking);

        using var vm = Create(picker: picker);
        vm.WorkingDirectory = _workspace;
        vm.WorkflowDirectory = _otherTracking;

        vm.BrowseWorkflowDirectoryCommand.Execute(null);

        Assert.Equal("Workflow-Verzeichnis auswählen", picker.Title);
        Assert.Equal(_otherTracking, picker.InitialDirectory);
        Assert.Equal(_tracking, vm.WorkflowDirectory);
    }

    [StaFact]
    public void BrowseWorkflowDirectory_Cancelled_KeepsTheCurrentChoice()
    {
        var picker = new RecordingDirectoryPicker(null);

        using var vm = Create(picker: picker);
        vm.WorkflowDirectory = _tracking;

        vm.BrowseWorkflowDirectoryCommand.Execute(null);

        Assert.Equal(_tracking, vm.WorkflowDirectory);
    }

    // -----------------------------------------------------------------------------------------
    // Restore must not look like a user pick
    // -----------------------------------------------------------------------------------------

    [StaFact]
    public void LoadForResume_DoesNotPushTheRestoredDirectoryThroughTheHistory()
    {
        // The restore assignments live inside LoadForResume's _suppressFolderSync block precisely
        // so the new OnWorkflowDirectoryChanged partial cannot treat a restored value as a fresh
        // user choice: the MRU write is not IsNameLocked-guarded.
        var paths = new TaskPaths(_workspace, TaskName);
        Directory.CreateDirectory(paths.TaskDirectory);

        var state = Journal();
        state.SubtasksEnabled = true;
        state.WorkflowDirectory = _tracking;

        using var vm = Create();

        vm.LoadForResume(new RecoverableTask(paths, state, WorkflowPhase.Implementation));

        Assert.True(vm.SubtasksEnabled);
        Assert.Equal(_tracking, vm.WorkflowDirectory);

        Assert.Empty(_settings.Settings.RecentWorkflowDirectories);
        Assert.Null(_settings.Settings.LastWorkflowDirectory);
        Assert.Empty(vm.RecentWorkflowDirectories);
    }
}
