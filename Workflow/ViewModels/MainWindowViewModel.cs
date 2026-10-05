using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Workflow.Models;
using Workflow.Services;

namespace Workflow.ViewModels;

/// <summary>The shell: the tab collection and the global buttons.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    /// <summary>Caption of the folder picker <c>Task laden</c> opens.</summary>
    private const string LoadTaskPickerTitle = "Task-Ordner auswählen";

    private readonly ITaskTabViewModelFactory _factory;
    private readonly ITaskRecoveryScanner _scanner;
    private readonly IDirectoryPickerService _picker;
    private readonly IMessageDialogService _messages;

    [ObservableProperty]
    private TaskTabViewModel? _selectedTab;

    /// <summary>Creates the shell view model and opens the initial tab.</summary>
    /// <param name="factory">Creates tab view models.</param>
    /// <param name="scanner">
    /// Finds interrupted tasks to offer as prefilled tabs, and reads the folder <c>Task laden</c> picks.
    /// </param>
    /// <param name="picker">Folder-browser dialog of <c>Task laden</c>.</param>
    /// <param name="messages">Shows why <c>Task laden</c> refused a folder.</param>
    /// <param name="startupErrors">Prompt-template validation errors, if any.</param>
    public MainWindowViewModel(
        ITaskTabViewModelFactory factory,
        ITaskRecoveryScanner scanner,
        IDirectoryPickerService picker,
        IMessageDialogService messages,
        IReadOnlyList<string> startupErrors)
    {
        _factory = factory;
        _scanner = scanner;
        _picker = picker;
        _messages = messages;
        StartupErrors = startupErrors;

        AddTaskTab();
    }

    /// <summary>Build identity shown top-right in the title bar, e.g. "v1.0.42".</summary>
    public static string VersionLabel => "v" + BuildInfo.Version;

    /// <summary>The open tabs; there is always at least one.</summary>
    public ObservableCollection<TaskTabViewModel> Tabs { get; } = [];

    /// <summary>Prompt-template problems found at startup. Blocks 'Start workflow' when non-empty.</summary>
    public IReadOnlyList<string> StartupErrors { get; }

    /// <summary>True when a prompt template is missing, empty or has an unknown token.</summary>
    public bool HasStartupErrors => StartupErrors.Count > 0;

    /// <summary>
    /// Offers every interrupted task the scan found as a prefilled tab. Deliberately not awaited
    /// before the window is shown: a slow or disconnected MRU entry must not delay startup.
    /// </summary>
    /// <returns>A task that completes once the recovered tabs have been added.</returns>
    public async Task InitialiseAsync()
    {
        IReadOnlyList<RecoverableTask> recovered;

        try
        {
            recovered = await _scanner.ScanAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        TaskTabViewModel? first = null;

        foreach (var task in recovered)
        {
            // 'Task laden' may already have opened this very task while the scan was running. A
            // second tab on one task folder would let two runs write one journal.
            if (FindOpenTab(task.Paths) is not null)
            {
                continue;
            }

            var tab = AddRecoveredTab(task);
            first ??= tab;
        }

        if (first is not null)
        {
            SelectedTab = first;
        }
    }

    /// <summary>Cancels and disposes every tab. Called from 'Schließen' and from Window.Closing.</summary>
    public void ShutdownAll()
    {
        foreach (var tab in Tabs.ToList())
        {
            tab.CloseRequested -= OnTabCloseRequested;
            tab.Dispose();
        }

        Tabs.Clear();
        SelectedTab = null;
    }

    partial void OnSelectedTabChanged(TaskTabViewModel? oldValue, TaskTabViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }
    }

    [RelayCommand]
    private void AddTaskTab()
    {
        var tab = _factory.Create();
        tab.CloseRequested += OnTabCloseRequested;
        Tabs.Add(tab);
        SelectedTab = tab;
    }

    /// <summary>
    /// <c>Task laden</c>: opens an existing task folder as a prefilled tab (Workflow_LOAD_AND_PAUSE
    /// spec section 5.3). A folder that cannot be a task folder, or that holds no readable journal,
    /// is refused with one error dialog; a task that is already open is selected, not loaded twice.
    /// </summary>
    [RelayCommand]
    private void LoadTask()
    {
        var chosen = _picker.PickDirectory(SelectedTab?.WorkingDirectory, LoadTaskPickerTitle);
        if (string.IsNullOrWhiteSpace(chosen))
        {
            return;
        }

        var paths = TaskPaths.FromTaskDirectory(chosen);
        if (paths is null)
        {
            _messages.ShowError(
                $"'{chosen}' kann kein Task-Ordner sein. Bitte den Ordner eines Tasks innerhalb eines Arbeitsverzeichnisses auswählen.");
            return;
        }

        // Asked BEFORE the journal is read: Load may write it (a demotion, the dismissed flag), and a
        // run in the open tab may be writing it at this very moment - whole-record
        // read-modify-writes with no lock between them.
        var open = FindOpenTab(paths);
        if (open is not null)
        {
            SelectedTab = open;
            return;
        }

        var task = _scanner.Load(paths);
        if (task is null)
        {
            _messages.ShowError(
                $"Im Ordner '{paths.TaskDirectory}' wurde kein Workflow-Task gefunden: Die Datei .workflow-state.json fehlt oder ist nicht lesbar. Bitte den Ordner eines Tasks auswählen, der mit dieser Anwendung gestartet wurde.");
            return;
        }

        SelectedTab = AddRecoveredTab(task);
    }

    // The one sequence that turns a journal into a tab, shared by the startup scan and 'Task laden'.
    private TaskTabViewModel AddRecoveredTab(RecoverableTask task)
    {
        var tab = _factory.Create();
        tab.CloseRequested += OnTabCloseRequested;
        tab.LoadForResume(task);
        Tabs.Add(tab);
        return tab;
    }

    // Compares canonical spellings of the task folder, ignoring case: Windows paths are
    // case-insensitive, and a tab whose name was typed rather than loaded names the same folder just
    // as well. Spellings, not file identities - junction/subst/8.3 aliases are out of scope (spec 3.2).
    private TaskTabViewModel? FindOpenTab(TaskPaths paths) =>
        Tabs.FirstOrDefault(tab =>
            !string.IsNullOrWhiteSpace(tab.WorkingDirectory)
            && !string.IsNullOrWhiteSpace(tab.TaskName)
            && string.Equals(
                CanonicalTaskDirectory(new TaskPaths(tab.WorkingDirectory, tab.TaskName)),
                CanonicalTaskDirectory(paths),
                StringComparison.OrdinalIgnoreCase));

    private static string CanonicalTaskDirectory(TaskPaths paths) =>
        TaskPaths.FromTaskDirectory(paths.TaskDirectory)?.TaskDirectory ?? paths.TaskDirectory;

    [RelayCommand]
    private void CloseTab(TaskTabViewModel? tab)
    {
        if (tab is null || !Tabs.Contains(tab))
        {
            return;
        }

        tab.CloseRequested -= OnTabCloseRequested;
        Tabs.Remove(tab);

        // Only a user-initiated close dismisses a recovered task. ShutdownAll must not: closing
        // the application is not a statement about any task.
        tab.NotifyClosedByUser();
        tab.Dispose();

        if (Tabs.Count == 0)
        {
            AddTaskTab();
            return;
        }

        SelectedTab ??= Tabs[^1];
    }

    [RelayCommand]
    private void CloseApplication()
    {
        ShutdownAll();
        Application.Current?.Shutdown();
    }

    private void OnTabCloseRequested(object? sender, EventArgs e)
    {
        if (sender is TaskTabViewModel tab)
        {
            CloseTab(tab);
        }
    }
}
