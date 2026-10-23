using System.Collections.ObjectModel;
using System.IO;
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
    private readonly IFilePickerService _filePicker;
    private readonly ITaskFolderService _folders;
    private readonly IMessageDialogService _messages;
    private readonly ISettingsService _settings;

    [ObservableProperty]
    private TaskTabViewModel? _selectedTab;

    /// <summary>Creates the shell view model. It opens no tab: tabs come from 'Neuer Task', 'Task laden' and the startup scan (WF_TABZUVIEL).</summary>
    /// <param name="factory">Creates tab view models.</param>
    /// <param name="scanner">
    /// Finds interrupted tasks to offer as prefilled tabs, and reads the folder <c>Task laden</c> picks.
    /// </param>
    /// <param name="picker">Folder-browser dialog of <c>Task laden</c>.</param>
    /// <param name="filePicker">File dialog that picks a misnamed spec or plan (Workflow_LoadFix spec 6).</param>
    /// <param name="folders">Finds and renames a task's misnamed spec and plan.</param>
    /// <param name="messages">Shows why <c>Task laden</c> refused a folder, and asks before renaming.</param>
    /// <param name="settings">
    /// Shared settings: their valid LastDirectory starts the 'Task laden' picker when no tab is selected
    /// (WF_TABZUVIEL).
    /// </param>
    /// <param name="startupErrors">Prompt-template validation errors, if any.</param>
    public MainWindowViewModel(
        ITaskTabViewModelFactory factory,
        ITaskRecoveryScanner scanner,
        IDirectoryPickerService picker,
        IFilePickerService filePicker,
        ITaskFolderService folders,
        IMessageDialogService messages,
        ISettingsService settings,
        IReadOnlyList<string> startupErrors)
    {
        _factory = factory;
        _scanner = scanner;
        _picker = picker;
        _filePicker = filePicker;
        _folders = folders;
        _messages = messages;
        _settings = settings;
        StartupErrors = startupErrors;
    }

    /// <summary>Build identity shown top-right in the title bar, e.g. "v1.0.42".</summary>
    public static string VersionLabel => "v" + BuildInfo.Version;

    /// <summary>The open tabs. Empty at startup and after the last tab closes; only 'Neuer Task' creates a blank one (WF_TABZUVIEL).</summary>
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
            tab.Finished -= OnTabFinished;
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
        Attach(tab);
        SelectedTab = tab;
    }

    /// <summary>
    /// <c>Task laden</c>: opens an existing task folder as a prefilled tab (Workflow_LOAD_AND_PAUSE
    /// spec section 5.3). A folder that cannot be a task folder, or whose journal cannot be read or
    /// written, is refused with one error dialog; a task that is already open is selected, not loaded
    /// twice. A folder without a journal gets one, and a misnamed spec and plan can be adopted first
    /// (Workflow_LoadFix spec sections 5 and 6).
    /// </summary>
    [RelayCommand]
    private void LoadTask()
    {
        var chosen = _picker.PickDirectory(
            SelectedTab is null ? ValidLastDirectory() : SelectedTab.WorkingDirectory,
            LoadTaskPickerTitle);
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

        // Workflow_LoadFix spec 6.2: BEFORE Load, so the reconciliation inside it already sees the
        // renamed files and records Specification as done instead of starting a new specification.
        if (!OfferPlanningArtefacts(paths))
        {
            return;
        }

        // Load creates a missing journal (spec 5), so null now means unreadable or not writable.
        var task = _scanner.Load(paths);
        if (task is null)
        {
            _messages.ShowError(
                $"Im Ordner '{paths.TaskDirectory}' konnte kein Workflow-Task geladen werden: Die Datei .workflow-state.json ist nicht lesbar oder konnte nicht angelegt werden.");
            return;
        }

        SelectedTab = AddRecoveredTab(task);
    }

    /// <summary>
    /// The folder a fresh tab would start in (same rule as <see cref="TaskTabViewModel"/>), used by
    /// 'Task laden' when no tab is selected (WF_TABZUVIEL).
    /// </summary>
    /// <returns>The saved last working directory when it is set and exists; otherwise null.</returns>
    private string? ValidLastDirectory()
    {
        var last = _settings.Settings.LastDirectory;
        return !string.IsNullOrWhiteSpace(last) && Directory.Exists(last) ? last : null;
    }

    /// <summary>
    /// Workflow_LoadFix spec section 6: when the task's spec or plan is missing under its canonical
    /// name but another Markdown file could be it, offers to pick the files and rename them.
    /// </summary>
    /// <param name="paths">The picked task folder's path set.</param>
    /// <returns>
    /// True when loading goes on: nothing to adopt, the user answered Nein, or every chosen file was
    /// renamed. False when the user cancelled a file dialog, or a choice was refused or failed; the
    /// load then ends without a journal write.
    /// </returns>
    private bool OfferPlanningArtefacts(TaskPaths paths)
    {
        var missing = _folders.FindMissingPlanningArtefacts(paths);
        if (missing.Count == 0)
        {
            return true;
        }

        // Workflow_LoadFix spec 6.2 (D21): a user's file is never renamed for a load that cannot
        // succeed. Load then runs, returns null and shows its error; every file stays as it was.
        if (!_scanner.CanLoad(paths))
        {
            return true;
        }

        var names = string.Join(", ", missing.Select(path => Path.GetFileName(path)));
        var adopt = _messages.Confirm(
            $"Im Ordner '{paths.TaskDirectory}' fehlen die Standarddateien des Tasks: {names}.\n\n"
            + "Vorhandene Dateien auswählen und in diese Namen umbenennen? Bei 'Nein' wird der Task ohne Umbenennen geladen.");

        if (!adopt)
        {
            return true;
        }

        var moves = new List<(string Source, string Target)>(missing.Count);

        foreach (var target in missing)
        {
            var source = _filePicker.PickMarkdownFile(paths.TaskDirectory, PickerTitle(paths, target));
            if (string.IsNullOrWhiteSpace(source))
            {
                // Cancelling is "stop", not "skip this one": nothing has been renamed yet (spec D5).
                return false;
            }

            moves.Add((source, target));
        }

        var refusal = RefuseChoices(paths, moves);
        if (refusal is not null)
        {
            _messages.ShowError(refusal);
            return false;
        }

        // Workflow_LoadFix spec 6.3 step 5 (D6): a pair is renamed completely or not at all, so a
        // failed move first undoes every move of this load that already succeeded.
        var done = new List<(string Source, string Target)>(moves.Count);
        try
        {
            foreach (var move in moves)
            {
                _folders.AdoptArtefact(move.Source, move.Target);
                done.Add(move);
            }
        }
        catch (IOException ex)
        {
            _messages.ShowError($"Die Datei konnte nicht umbenannt werden: {ex.Message}{UndoMoves(done)}");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            _messages.ShowError($"Kein Zugriff beim Umbenennen: {ex.Message}{UndoMoves(done)}");
            return false;
        }

        return true;
    }

    // Spec 6.3 step 5: moves back newest first, with the same AdoptArtefact. An undo that fails is
    // named in the one error dialog and the next undo is still tried. A 0-byte placeholder the
    // forward move deleted is not recreated - it held no content.
    private string UndoMoves(List<(string Source, string Target)> done)
    {
        var failures = new List<string>();
        for (var i = done.Count - 1; i >= 0; i--)
        {
            var (source, target) = done[i];
            try
            {
                _folders.AdoptArtefact(target, source);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"\n\n'{target}' konnte nicht nach '{source}' zurückbenannt werden: {ex.Message}");
            }
        }

        return string.Concat(failures);
    }

    private static string PickerTitle(TaskPaths paths, string target) =>
        string.Equals(target, paths.SpecAbsolute, StringComparison.OrdinalIgnoreCase)
            ? $"Spezifikation auswählen (wird zu {Path.GetFileName(target)})"
            : $"Plan auswählen (wird zu {Path.GetFileName(target)})";

    // Spec 6.3 step 3, checked before anything moves: one file cannot be both, and a file that is
    // already one of the task's own would vacate the slot it fills.
    private static string? RefuseChoices(TaskPaths paths, List<(string Source, string Target)> moves)
    {
        if (moves.Count == 2 && SamePath(moves[0].Source, moves[1].Source))
        {
            return "Spezifikation und Plan müssen zwei verschiedene Dateien sein.";
        }

        string[] taskFiles =
        [
            paths.SpecAbsolute,
            paths.PlanAbsolute,
            paths.ReviewAbsolute,
            paths.DoneAbsolute,
            paths.StateAbsolute,
        ];

        foreach (var (source, _) in moves)
        {
            if (taskFiles.Any(file => SamePath(file, source)))
            {
                return $"'{source}' ist bereits eine Datei des Tasks und kann nicht umbenannt werden.";
            }
        }

        return null;
    }

    private static bool SamePath(string first, string second) =>
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);

    // The one sequence that turns a journal into a tab, shared by the startup scan and 'Task laden'.
    private TaskTabViewModel AddRecoveredTab(RecoverableTask task)
    {
        var tab = _factory.Create();
        tab.LoadForResume(task);
        Attach(tab);
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
        tab.Finished -= OnTabFinished;
        Tabs.Remove(tab);

        // WF_QUEUE spec Q11: FIRST drop the queues that waited on this tab, THEN take it out of the lists.
        // Removing the selected item from a Selector's ItemsSource makes the view write null into the
        // TwoWay SelectedItem, which would clear QueuedAfter before CancelIfQueuedAfter could see it and
        // say why. The lists change item by item, never rebuilt, for the same reason.
        foreach (var other in Tabs)
        {
            other.CancelIfQueuedAfter(tab);
            other.QueueCandidates.Remove(tab);
        }

        // Only a user-initiated close dismisses a recovered task. ShutdownAll must not: closing
        // the application is not a statement about any task.
        tab.NotifyClosedByUser();
        tab.Dispose();

        if (Tabs.Count == 0)
        {
            // No replacement tab: 'Neuer Task' opens the next one, and the closed tab must not stay
            // selected (WF_TABZUVIEL).
            SelectedTab = null;
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

    // The one sequence that opens a tab (WF_QUEUE spec Q2): wires the tab's events and gives it and every
    // open tab the other as a queue candidate. A finished tab is not offered to the newcomer, but the
    // newcomer is offered to it. Lists are updated item by item, never rebuilt (Selector trap, Q8).
    private void Attach(TaskTabViewModel tab)
    {
        tab.CloseRequested += OnTabCloseRequested;
        tab.Finished += OnTabFinished;

        foreach (var other in Tabs)
        {
            other.QueueCandidates.Add(tab);
            if (!other.IsTaskFinished)
            {
                tab.QueueCandidates.Add(other);
            }
        }

        Tabs.Add(tab);
    }

    // WF_QUEUE spec Q8, Q9, Q10: starts the tabs queued after the finished one, in tab order.
    private void OnTabFinished(object? sender, EventArgs e)
    {
        if (sender is not TaskTabViewModel finished)
        {
            return;
        }

        var started = new List<TaskTabViewModel>();
        foreach (var tab in Tabs.ToList())
        {
            // FIRST the queue, THEN the list (Q8): taking the finished tab out of QueueCandidates first
            // would make the Selector null QueuedAfter and the start would never happen.
            // A tab that shares its working directory with one just started waits for that one instead
            // (Q10): two runs must not write the same folder. Only a still-running sibling holds
            // anything up; a run that already ended is done with the directory.
            var sibling = ReferenceEquals(tab.QueuedAfter, finished)
                ? started.Find(s => s.IsRunning && SharesWorkingDirectory(s, tab))
                : null;
            if (sibling is not null)
            {
                tab.MoveQueueAfter(finished, sibling);
            }
            else if (tab.StartIfQueuedAfter(finished))
            {
                started.Add(tab);
            }

            tab.QueueCandidates.Remove(finished);
        }
    }

    // Spellings only: junctions and subst drives are not resolved (WF_QUEUE spec E16).
    private static bool SharesWorkingDirectory(TaskTabViewModel first, TaskTabViewModel second)
    {
        if (string.IsNullOrWhiteSpace(first.WorkingDirectory) || string.IsNullOrWhiteSpace(second.WorkingDirectory))
        {
            return false;
        }

        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first.WorkingDirectory)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second.WorkingDirectory)),
            StringComparison.OrdinalIgnoreCase);
    }
}
