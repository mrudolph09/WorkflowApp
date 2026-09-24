using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;
using Workflow.Models;
using Workflow.Services;

namespace Workflow.ViewModels;

/// <summary>One Task, one tab: metadata, the four indicators, and the terminal.</summary>
/// <remarks>
/// The tab is the live, editable subtask configuration of design issue 3: it implements
/// <see cref="ISubtaskConfiguration"/> so that <see cref="SubtaskConfiguration.Capture"/> can take
/// the one snapshot the run carries. The run is handed that capture <em>deferred</em>
/// (<see cref="SubtaskConfiguration.Deferred"/>), so it is taken at phase-4 entry and the user may
/// still change the configuration while phases 1-3 run; from that moment on it is one immutable
/// record. Nothing downstream ever holds the tab itself, so the enabled flag and the tracking
/// directory cannot be observed at two different moments (requirement 1.6).
/// </remarks>
public sealed partial class TaskTabViewModel : ObservableObject, ISubtaskConfiguration, IDisposable
{
    private readonly ITaskFolderService _folders;
    private readonly IWorkflowOrchestrator _orchestrator;
    private readonly ISettingsService _settings;
    private readonly ITaskStateStore _stateStore;
    private readonly IDirectoryPickerService _picker;
    private readonly ManualPhaseSignal _manualSignal = new();
    private readonly TimeSpan _folderDebounce;

    private readonly IReadOnlyList<string> _startupErrors;

    private CancellationTokenSource? _run;

    // The handle on the in-flight run. StartWorkflow deliberately does not await it - see
    // RunAsync - but requirement 5.3 needs SOMETHING to await, so the task is kept rather than
    // discarded. It is never awaited from Dispose: teardown cancels and moves on.
    private Task? _runTask;
    private CancellationTokenSource? _folderDebounceSource;
    private string? _folderOnDisk;
    private WorkflowPhase? _activePhase;
    private bool _suppressFolderSync;
    private int _disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    [NotifyCanExecuteChangedFor(nameof(StartWorkflowCommand))]
    private string _taskName = string.Empty;

    [ObservableProperty]
    private string _taskDescription = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartWorkflowCommand))]
    private string? _workingDirectory;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartWorkflowCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isNameLocked;

    /// <summary>
    /// True while this tab is the one shown. The tab template keeps every tab's view alive and
    /// toggles visibility on this flag, so each tab owns its WebView2 for its whole lifetime.
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartWorkflowCommand))]
    private string? _validationMessage;

    [ObservableProperty]
    private string? _infoMessage;

    /// <summary>
    /// Why the selected tracking directory cannot be used, or null while subtask mode is off or
    /// the selection is usable (requirement 1.3).
    /// </summary>
    /// <remarks>
    /// A channel of its own rather than a second writer of <see cref="ValidationMessage"/>, for two
    /// reasons. <see cref="SyncFolder"/> assigns <see cref="ValidationMessage"/> unconditionally on
    /// every name and working-directory change, so a tracking message written there would be
    /// cleared by the next keystroke while the start gate stayed closed - or survive after the
    /// user fixed the directory. And the two messages describe different rows: this one is shown
    /// beside the <c>Workflow-Verzeichnis</c> selector and disappears with it when subtask mode is
    /// deselected. It is part of <see cref="CanStartWorkflow"/> because requirement 1.3 asks for
    /// the start action to be disabled, not merely annotated.
    /// </remarks>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartWorkflowCommand))]
    private string? _workflowDirectoryMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartButtonLabel))]
    private WorkflowPhase? _resumePhase;

    [ObservableProperty]
    private bool _isRecovered;

    /// <summary>
    /// Whether this task's implementation phase runs as subtasks (requirement 1.1). Editable while
    /// phases 1-3 run; read exactly once, at capture time.
    /// </summary>
    [ObservableProperty]
    private bool _subtasksEnabled;

    /// <summary>
    /// The tracking repository selected for subtask mode, or null while none is chosen
    /// (requirement 1.2). Distinct from <see cref="WorkingDirectory"/>, which is the product
    /// working directory the sessions run in.
    /// </summary>
    [ObservableProperty]
    private string? _workflowDirectory;

    /// <summary>Creates the tab.</summary>
    /// <param name="folders">Task-folder service.</param>
    /// <param name="orchestrator">The four-phase state machine.</param>
    /// <param name="settings">Shared settings, used for the directory MRU.</param>
    /// <param name="stateStore">The per-task workflow journal, read to re-arm Continue.</param>
    /// <param name="picker">Folder-browser dialog.</param>
    /// <param name="terminal">This tab's terminal.</param>
    /// <param name="folderDebounce">Delay before a name change touches the disk.</param>
    /// <param name="startupErrors">
    /// Prompt-template problems found at startup. While this list is non-empty
    /// <c>StartWorkflowCommand</c> cannot execute on this tab (F17, spec section 9.4).
    /// </param>
    public TaskTabViewModel(
        ITaskFolderService folders,
        IWorkflowOrchestrator orchestrator,
        ISettingsService settings,
        ITaskStateStore stateStore,
        IDirectoryPickerService picker,
        TerminalViewModel terminal,
        TimeSpan folderDebounce,
        IReadOnlyList<string> startupErrors)
    {
        ArgumentNullException.ThrowIfNull(startupErrors);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(stateStore);

        _folders = folders;
        _orchestrator = orchestrator;
        _settings = settings;
        _stateStore = stateStore;
        _picker = picker;
        _folderDebounce = folderDebounce;
        _startupErrors = startupErrors;

        if (startupErrors.Count > 0)
        {
            // The modal at startup can be dismissed; the tab keeps saying why Start is dead.
            ValidationMessage = startupErrors[0];
        }

        Terminal = terminal;
        Phases = new ObservableCollection<PhaseIndicatorViewModel>(
            PhaseCatalog.All.Select(d => new PhaseIndicatorViewModel(d)));
        RecentDirectories = new ObservableCollection<string>(settings.Settings.RecentDirectories);

        // A settings file carrying "RecentWorkflowDirectories": null reaches us as a null
        // collection: System.Text.Json assigns the JSON null straight over the property
        // initializer instead of leaving the empty list in place. The list is restored rather than
        // merely tolerated, because SettingsService.Promote dereferences it - a read-side null
        // check here would still throw the moment the user picks a tracking directory.
        settings.Settings.RecentWorkflowDirectories ??= [];

        RecentWorkflowDirectories =
            new ObservableCollection<string>(settings.Settings.RecentWorkflowDirectories);

        if (!string.IsNullOrWhiteSpace(settings.Settings.LastDirectory)
            && Directory.Exists(settings.Settings.LastDirectory))
        {
            WorkingDirectory = settings.Settings.LastDirectory;
        }

        // Requirement 1.4: the tracking directory is offered again on a new tab, from its own
        // history rather than the working-directory one.
        if (!string.IsNullOrWhiteSpace(settings.Settings.LastWorkflowDirectory)
            && Directory.Exists(settings.Settings.LastWorkflowDirectory))
        {
            WorkflowDirectory = settings.Settings.LastWorkflowDirectory;
        }
    }

    /// <summary>Raised when the tab's close button is clicked.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The tab header: the task name, or '(Bezeichnung)' while it is empty.</summary>
    public string Header => string.IsNullOrWhiteSpace(TaskName) ? "(Bezeichnung)" : TaskName.Trim();

    /// <summary>The Start button's caption: 'Continue workflow' once a resume point is known.</summary>
    public string StartButtonLabel => ResumePhase is null ? "Start workflow" : "Continue workflow";

    /// <summary>The four station indicators.</summary>
    public ObservableCollection<PhaseIndicatorViewModel> Phases { get; }

    /// <summary>Working directories offered in the ComboBox.</summary>
    public ObservableCollection<string> RecentDirectories { get; }

    /// <summary>Tracking directories offered in the <c>Workflow-Verzeichnis</c> ComboBox.</summary>
    /// <remarks>
    /// A list of its own, kept from <see cref="AppSettings.RecentWorkflowDirectories"/>: the
    /// product working directory and the tracking repository are independent choices
    /// (requirement 1.4). Reconciled by <see cref="SyncRecentWorkflowDirectories"/>, which never
    /// clears, for the reason documented on <see cref="SyncRecentDirectories"/>.
    /// </remarks>
    public ObservableCollection<string> RecentWorkflowDirectories { get; }

    /// <summary>
    /// False while phase 4 is running, which is when requirement 1.6 freezes the subtask
    /// configuration: the checkbox, the tracking-directory selector and the folder-picker button.
    /// </summary>
    /// <remarks>
    /// Derived from the same <c>_activePhase</c> field the task-completion commands read, so
    /// <see cref="ApplyProgress"/> - the only writer - raises the change notification for it. Phases
    /// 1 to 3 deliberately leave the configuration editable: the snapshot the run carries is taken
    /// at phase-4 entry, not at run entry, so an edit made while they run still reaches the run.
    /// </remarks>
    public bool IsSubtaskConfigurationEditable => _activePhase != WorkflowPhase.Implementation;

    /// <summary>This tab's terminal.</summary>
    public TerminalViewModel Terminal { get; }

    /// <summary>
    /// The second grey/yellow/green indicator, shown beside the phase indicators while subtask mode
    /// is selected (requirement 3.1).
    /// </summary>
    /// <remarks>
    /// The tab owns the instance for its whole lifetime and never replaces it, so a binding made
    /// once keeps working; its contents are driven through <see cref="SubtaskIndicatorViewModel.Apply"/>
    /// and <see cref="SubtaskIndicatorViewModel.Reset"/>.
    /// </remarks>
    public SubtaskIndicatorViewModel SubtaskIndicator { get; } = new();

    /// <summary>Applies a phase status change from the orchestrator. Public for testing.</summary>
    /// <param name="progress">The reported change.</param>
    public void ApplyProgress(PhaseProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var indicator = Phases.First(p => p.Phase == progress.Phase);
        indicator.Status = progress.Status;

        _activePhase = progress.Status == PhaseStatus.Active ? progress.Phase : null;

        CompleteTaskCommand.NotifyCanExecuteChanged();

        // _activePhase is a plain field, so nothing else announces the editing lock. Without these
        // two lines the XAML IsEnabled bindings would be evaluated once and never again, and the
        // phase-4 lock of requirement 1.6 would exist only inside this class.
        OnPropertyChanged(nameof(IsSubtaskConfigurationEditable));
        BrowseWorkflowDirectoryCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Prefills this tab from an interrupted task found by the startup scan.</summary>
    /// <param name="task">The task to restore.</param>
    public void LoadForResume(RecoverableTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        // Every assignment below would otherwise schedule the debounced folder sync, which could
        // try to create - or worse, RENAME - a folder that already holds this task's artefacts.
        _suppressFolderSync = true;

        try
        {
            WorkingDirectory = task.Paths.WorkingDirectory;
            TaskDescription = task.State.TaskDescription;
            TaskName = task.Paths.TaskName;

            // The folder and the field now agree, so no rename can ever be attempted.
            _folderOnDisk = task.Paths.TaskName;

            // The artefact names embed the task name (T_spec.md, T_plan.md, T-review.md,
            // T-done.md) and the paths are already written into the spec and plan on disk.
            // Renaming would orphan all of them, so the name is frozen - the same argument
            // BASE section 8.4 makes for a running pipeline.
            IsNameLocked = true;

            // Requirement 4.1. Restored inside the suppression block for the same reason the
            // fields above are: these two setters belong to the subtask configuration the tab
            // owns, and letting the working-folder synchronisation run between them would
            // recompute resume state - and, once the subtask row is bound, push a restored
            // directory through the MRU as though the user had just picked it.
            SubtasksEnabled = task.State.SubtasksEnabled;
            WorkflowDirectory = task.State.WorkflowDirectory;

            ApplyJournal(task.State);
            SeedSubtaskIndicatorFromLedger(task.Paths.TaskName);
            ResumePhase = task.ResumePhase;
            IsRecovered = true;
        }
        finally
        {
            _suppressFolderSync = false;
        }

        // The startup prompt-template gate still wins over everything (BASE section 9.4).
        ValidationMessage = _startupErrors.Count > 0
            ? _startupErrors[0]
            : _folders.Validate(TaskName, WorkingDirectory) is { IsValid: false } invalid
                ? invalid.ErrorMessage
                : null;
    }

    /// <summary>
    /// Called when the user closes this tab, as opposed to the application shutting down.
    /// A recovered task is then not offered again until it is continued (SPEC section 6.7).
    /// </summary>
    public void NotifyClosedByUser()
    {
        if (!IsRecovered || string.IsNullOrWhiteSpace(WorkingDirectory) || string.IsNullOrWhiteSpace(TaskName))
        {
            return;
        }

        _stateStore.SetDismissed(new TaskPaths(WorkingDirectory, TaskName), dismissed: true);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _run?.Cancel();
        _run?.Dispose();
        _folderDebounceSource?.Cancel();
        _folderDebounceSource?.Dispose();
        Terminal.Dispose();
    }

    // An Active entry means the application died while that phase was running. It is about to be
    // re-run, so it is shown grey, not yellow.
    private void ApplyJournal(TaskState state)
    {
        foreach (var entry in state.Phases)
        {
            var indicator = Phases.FirstOrDefault(p => p.Phase == entry.Phase);
            if (indicator is not null)
            {
                indicator.Status = entry.Status == PhaseStatus.Active ? PhaseStatus.Pending : entry.Status;
            }
        }
    }

    /// <summary>
    /// Derives the subtask indicator's counts and failure reasons from the tracking files, so a
    /// recovered tab shows what is on disk before any session starts (requirements 4.1 and 4.5).
    /// </summary>
    /// <param name="taskName">The task name the tracking folder is named after.</param>
    /// <remarks>
    /// <para>
    /// There is no persisted cursor to read (requirement 4.5): the ledger is read fresh and the
    /// counts fall out of it. The stage seeded is <see cref="SubtaskStage.Idle"/> - grey - because
    /// nothing is running yet; requirement 3.4 reserves yellow for "while running", and the design
    /// says a recovered tab's implementation indicator may initially be grey while its counts still
    /// reflect disk. A task whose entries are all complete is green regardless, because
    /// <see cref="SubtaskIndicatorViewModel"/> derives green from the counts alone.
    /// </para>
    /// <para>
    /// Every unusable case - mode off, no directory, a blank or malformed stored path, or an index
    /// that cannot be read - resets the indicator rather than guessing. Zero counts can never be
    /// green, so unreadable evidence is structurally incapable of completing a task
    /// (requirement 4.6). Naming that condition in German is not done here, because 4.6 is not among
    /// this task's requirements - it belongs to section 6. When it is surfaced, the channel must be
    /// <see cref="InfoMessage"/> and not <see cref="ValidationMessage"/>: the latter is part of
    /// <see cref="CanStartWorkflow"/>, so writing the condition there would disable the start action
    /// and make the recoverable task unrecoverable - the opposite of what 4.6 asks for.
    /// </para>
    /// <para>
    /// This runs on the dispatcher, during the startup restore, and reads roughly two small files
    /// per subtask once per recovered tab - the same order of synchronous disk work
    /// <see cref="SyncFolder"/> already performs there.
    /// </para>
    /// </remarks>
    private void SeedSubtaskIndicatorFromLedger(string taskName)
    {
        if (!SubtasksEnabled || WorkflowDirectory is null)
        {
            SubtaskIndicator.Reset();
            return;
        }

        SubtaskPaths tracking;

        try
        {
            tracking = new SubtaskPaths(WorkflowDirectory, taskName);
        }
        catch (ArgumentException)
        {
            // The journal stores the chosen directory verbatim, so a blank or whitespace-only
            // value survives to here by design; it is a user condition, not a defect.
            SubtaskIndicator.Reset();
            return;
        }

        var snapshot = SubtaskLedger.TryRead(tracking);

        if (snapshot is null)
        {
            SubtaskIndicator.Reset();
            return;
        }

        SubtaskIndicator.Apply(new SubtaskProgress(
            SubtaskStage.Idle,
            snapshot.Completed,
            snapshot.Total,
            snapshot.Failed,
            null,
            snapshot.States));
    }

    // SPEC section 6.6. Two outcomes only: a resumable journal paints the indicators and sets
    // ResumePhase, and anything else - no journal, or a finished one - resets BOTH.
    private void RefreshResumeStateFromJournal(TaskPaths paths)
    {
        // A recovered tab's resume state came from LoadForResume and must not be recomputed.
        // StartWorkflow calls SyncFolder directly, bypassing the IsNameLocked guard in
        // ScheduleFolderSync (that is how a folder deleted between scan and Continue is
        // recreated), so without this the journal would overwrite ResumePhase microseconds before
        // it is handed to the orchestrator - and would repaint indicators the orchestrator is
        // about to drive.
        if (IsRecovered)
        {
            return;
        }

        var state = _stateStore.TryLoad(paths);

        // The same demote-never-promote rule the startup scan applies, from the same helper: an
        // unreconciled re-arm would resume into a phase whose inputs have been deleted, which is
        // exactly what the rule exists to prevent (SPEC section 6.3.2).
        if (state is not null && PhaseReconciliation.Reconcile(paths, state))
        {
            _stateStore.ReplacePhases(paths, [.. state.Phases]);
        }

        var next = state is null ? null : PhaseReconciliation.FirstIncomplete(state);

        if (state is null || next is null)
        {
            // No journal, or a finished task: this tab is a fresh start and must look like one.
            // Leaving the indicators alone would keep painting the PREVIOUSLY typed task's green
            // phases onto this one, or show four green phases above a 'Start workflow' button.
            ResetPhaseIndicators();
            ResumePhase = null;
            return;
        }

        ApplyJournal(state);
        ResumePhase = next;
    }

    private void ResetPhaseIndicators()
    {
        foreach (var indicator in Phases)
        {
            indicator.Status = PhaseStatus.Pending;
        }
    }

    partial void OnTaskNameChanged(string value) => ScheduleFolderSync();

    partial void OnWorkingDirectoryChanged(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        // The MRU stores the normalised form (see WorkingDirectoryPath). If this property kept the
        // raw picker result - 'C:\work\' against a stored 'C:\work' - the ComboBox's SelectedItem
        // would match no item and the Selector would push null straight back in here.
        var normalised = WorkingDirectoryPath.Normalise(value);
        if (!string.Equals(normalised, value, StringComparison.Ordinal))
        {
            WorkingDirectory = normalised;
            return;
        }

        _settings.AddRecentDirectory(normalised);
        _settings.Save();
        SyncRecentDirectories();

        _folderOnDisk = null;
        ScheduleFolderSync();
    }

    // Requirement 1.2: the tracking row exists only in subtask mode, so its verdict has to be
    // recomputed when the mode is toggled - a directory that blocks the start action must stop
    // blocking it the moment the user goes back to a normal run.
    partial void OnSubtasksEnabledChanged(bool value)
    {
        RefreshWorkflowDirectoryValidation();

        if (!value)
        {
            // The indicator belongs to subtask mode (requirement 3.1) and disappears with the row.
            // Left as it was, it would come back carrying the counts of a mode the task no longer
            // runs in the moment the checkbox is ticked again. Only the OFF direction clears:
            // ticking the box on must not wipe what the ledger seeded on a recovered tab
            // (requirement 4.5), and LoadForResume restores this very property.
            SubtaskIndicator.Reset();
        }
    }

    partial void OnWorkflowDirectoryChanged(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            // Blank is a real stored value (a journal keeps the user's choice verbatim), not a
            // programming error, and Normalise would throw on it.
            RefreshWorkflowDirectoryValidation();
            return;
        }

        // Same trap as OnWorkingDirectoryChanged: the MRU stores the normalised form, so a raw
        // picker result with a trailing separator would match no item and the TwoWay Selector
        // would push null straight back in here.
        var normalised = WorkingDirectoryPath.Normalise(value);
        if (!string.Equals(normalised, value, StringComparison.Ordinal))
        {
            WorkflowDirectory = normalised;
            return;
        }

        var validation = WorkflowDirectoryValidation.Validate(normalised);
        WorkflowDirectoryMessage = SubtasksEnabled ? validation.ErrorMessage : null;

        // LoadForResume restores this property inside its _suppressFolderSync block precisely for
        // the lines below: a value that came back from the journal is not a fresh user choice, and
        // promoting it would reorder the history - and rewrite LastWorkflowDirectory - on every
        // restart. Unlike ScheduleFolderSync, this write is not IsNameLocked-guarded, so the flag
        // is the only thing standing between a restore and the MRU.
        //
        // Requirement 1.4 also offers the choice again only once it is a usable tracking
        // repository, so a rejected directory is shown, not remembered.
        if (_suppressFolderSync || !validation.IsValid)
        {
            return;
        }

        _settings.AddRecentWorkflowDirectory(normalised);
        _settings.Save();
        SyncRecentWorkflowDirectories();
    }

    private void RefreshWorkflowDirectoryValidation() =>
        WorkflowDirectoryMessage = SubtasksEnabled
            ? WorkflowDirectoryValidation.Validate(WorkflowDirectory).ErrorMessage
            : null;

    // Deliberately never Clear()s. TaskTabView.xaml binds this collection to the ComboBox's
    // ItemsSource while SelectedItem is bound TwoWay to WorkingDirectory, so emptying it makes the
    // Selector drop the selection and write null back into WorkingDirectory. Re-adding the entries
    // afterwards does not restore the selection: the box goes blank, validation reports a missing
    // working directory and 'Start workflow' dies the moment a second directory is picked.
    private void SyncRecentDirectories() =>
        Reconcile(RecentDirectories, _settings.Settings.RecentDirectories);

    // The same never-Clear() reconciliation for the tracking-directory ComboBox, which binds its
    // SelectedItem TwoWay to WorkflowDirectory and would be blanked by a rebuild in exactly the
    // same way.
    private void SyncRecentWorkflowDirectories() =>
        Reconcile(RecentWorkflowDirectories, _settings.Settings.RecentWorkflowDirectories);

    private static void Reconcile(ObservableCollection<string> shown, Collection<string> desired)
    {
        for (var i = shown.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(shown[i]))
            {
                shown.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var at = shown.IndexOf(desired[i]);

            if (at < 0)
            {
                shown.Insert(i, desired[i]);
            }
            else if (at != i)
            {
                shown.Move(at, i);
            }
        }
    }

    private void ScheduleFolderSync()
    {
        // The name is frozen once the pipeline starts: a running CLI holds the directory and
        // Directory.Move would throw. Locking removes the failure mode instead of handling it.
        if (IsNameLocked)
        {
            return;
        }

        // Set while RollBackNameToDisk restores TaskName after a failed rename; without it the
        // restoring assignment would schedule another sync and retry the rename in a loop.
        if (_suppressFolderSync)
        {
            return;
        }

        _folderDebounceSource?.Cancel();
        _folderDebounceSource?.Dispose();
        _folderDebounceSource = new CancellationTokenSource();
        var token = _folderDebounceSource.Token;

        if (_folderDebounce <= TimeSpan.Zero)
        {
            SyncFolder();
            return;
        }

        _ = SyncFolderAfterDebounceAsync(token);
    }

    // Deliberately NOT Task.Run. ScheduleFolderSync always runs on the UI thread (every caller is
    // a property setter driven by a binding), so awaiting here captures the dispatcher's
    // SynchronizationContext and SyncFolder resumes on it. On a pool thread instead, the
    // ValidationMessage setter's [NotifyCanExecuteChangedFor] raises ICommand.CanExecuteChanged
    // off the UI thread, WPF's ButtonBase handler touches Button.IsEnabled from there and throws
    // a cross-thread InvalidOperationException. Nothing awaits this task, so that exception is
    // swallowed: SyncFolder never reaches EnsureCreated and 'Start workflow' stays disabled for
    // the rest of the session even though CanStartWorkflow() is true.
    private async Task SyncFolderAfterDebounceAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(_folderDebounce, token);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a later keystroke.
            return;
        }

        SyncFolder();
    }

    private void SyncFolder()
    {
        // A startup prompt-template problem (F17, spec section 9.4) takes precedence over folder
        // validation: Start workflow is dead regardless of the name/directory, and the message
        // saying why must survive the dialog being dismissed, not get clobbered by a valid name.
        if (_startupErrors.Count > 0)
        {
            ValidationMessage = _startupErrors[0];
            return;
        }

        var validation = _folders.Validate(TaskName, WorkingDirectory);
        ValidationMessage = validation.IsValid ? null : validation.ErrorMessage;

        if (!validation.IsValid || string.IsNullOrWhiteSpace(WorkingDirectory))
        {
            return;
        }

        var paths = new TaskPaths(WorkingDirectory, TaskName);

        try
        {
            if (_folderOnDisk is not null && !string.Equals(_folderOnDisk, paths.TaskName, StringComparison.Ordinal))
            {
                _folders.Rename(WorkingDirectory, _folderOnDisk, paths.TaskName);
                _folderOnDisk = paths.TaskName;
                InfoMessage = null;

                // Typing the name of an unfinished task - including one the user dismissed by
                // closing its recovered tab - must bring back 'Continue workflow'. This is what
                // makes the dismissal non-destructive. Called on BOTH successful exits: this
                // branch returns before the create branch is reached, so a single call at the end
                // of the method would leave the postcondition holding on one path only.
                RefreshResumeStateFromJournal(paths);
                return;
            }

            InfoMessage = _folders.DirectoryAlreadyExisted(paths)
                ? "Ordner existiert bereits und wird weiterverwendet."
                : null;

            _folders.EnsureCreated(paths);
            _folderOnDisk = paths.TaskName;

            RefreshResumeStateFromJournal(paths);
        }
        catch (IOException ex)
        {
            ValidationMessage = $"Der Ordner konnte nicht angelegt oder umbenannt werden: {ex.Message}";
            RollBackNameToDisk();
        }
        catch (UnauthorizedAccessException ex)
        {
            ValidationMessage = $"Kein Zugriff auf das Arbeitsverzeichnis: {ex.Message}";
            RollBackNameToDisk();
        }
    }

    /// <summary>
    /// Restores <see cref="TaskName"/> to the folder that actually exists on disk after a failed
    /// rename (spec sections 8.3 and 12.2).
    /// </summary>
    /// <remarks>
    /// Leaving TaskName and the on-disk folder disagreeing is worse than the failure itself: the
    /// tab header, every path in TaskPaths and the {taskbezeichnung} token would all name a
    /// folder that does not exist, and the user has no way to tell which one is authoritative.
    /// The guard stops the restoring assignment from re-entering the debounced folder sync -
    /// which would retry the rename that just failed, in a loop.
    /// </remarks>
    private void RollBackNameToDisk()
    {
        if (_folderOnDisk is null || string.Equals(TaskName, _folderOnDisk, StringComparison.Ordinal))
        {
            return;
        }

        _suppressFolderSync = true;
        try
        {
            TaskName = _folderOnDisk;
        }
        finally
        {
            _suppressFolderSync = false;
        }
    }

    private bool CanStartWorkflow() =>
        !IsRunning
        // F17 / spec section 9.4: a prompt-template problem disables Start on EVERY tab,
        // including tabs opened with '+' after the startup dialog was dismissed. Holding the
        // flag only on MainWindowViewModel leaves the button executable once the modal is gone.
        && _startupErrors.Count == 0
        && ValidationMessage is null
        // Requirement 1.3: subtask mode with an unusable tracking directory disables starting a
        // workflow. The property is null whenever subtask mode is off, so a normal run is
        // unaffected by a stale tracking selection.
        && WorkflowDirectoryMessage is null
        && !string.IsNullOrWhiteSpace(TaskName)
        && !string.IsNullOrWhiteSpace(WorkingDirectory);

    [RelayCommand(CanExecute = nameof(CanStartWorkflow))]
    private void StartWorkflow()
    {
        SyncFolder();

        // The marker can be removed from an already-selected directory, and nothing announces
        // that: the verdict is about the file system, not about a property. The start action
        // re-derives it for the same reason it calls SyncFolder - a gate that trusts a stale
        // answer is not a gate (requirement 1.3).
        RefreshWorkflowDirectoryValidation();

        if (!CanStartWorkflow() || string.IsNullOrWhiteSpace(WorkingDirectory))
        {
            return;
        }

        IsNameLocked = true;
        IsRunning = true;

        var startPhase = ResumePhase ?? WorkflowPhase.Specification;

        // Requirement 3.4: the subtask indicator is grey before phase 4, and it cannot be grey
        // "with counts" - a row reading '3 von 6' while phase 1 runs describes work this run has
        // not looked at. Anything the row was showing therefore goes now, AFTER the start gate, so
        // a refused start never destroys what the user is reading while they fix it.
        //
        // The one exception is a run that starts AT phase 4. Its counts are not a previous run's
        // leftovers: LoadForResume read them off the tracking files moments ago (requirement 4.5)
        // and they are exactly the state this run continues from, so the grey-before-phase-4 rule
        // has nothing left to say about them. Resetting there would make the tab claim '0 von 0'
        // work done - and keep claiming it if the run failed before publishing anything, which the
        // phase-4 configuration check can do (requirement 1.8).
        if (startPhase != WorkflowPhase.Implementation)
        {
            SubtaskIndicator.Reset();
        }

        var paths = new TaskPaths(WorkingDirectory, TaskName);

        // Persist the description BEFORE the run: a crash one second from now must still recover
        // a tab with its Taskbeschreibung intact.
        _stateStore.SaveDescription(paths, TaskDescription);

        _run = new CancellationTokenSource();

        var request = new WorkflowRunRequest(
            paths,
            TaskDescription,
            Terminal,
            _manualSignal,
            new Progress<PhaseProgress>(ApplyProgress),
            startPhase,
            // Requirement 4.2: without this a continued subtask task takes the normal
            // implementation branch and the skip-completed loop is never reached.
            //
            // Deferred, not Capture: requirement 1.6 puts the snapshot at phase-4 entry, and this
            // is run entry. The two coincide only for a resumed task; for a run started at phase 1
            // the user may still tick the checkbox or pick a directory while phases 1-3 run, and
            // capturing here would silently drop that edit. What travels is therefore a once-only
            // capture the orchestrator forces when it enters phase 4 - still a snapshot from that
            // moment on, still nothing the orchestrator can re-read.
            //
            // Deferred, not a validating variant, for the same two reasons as before: the start
            // gate checks the directory before the run begins, the orchestrator re-validates the
            // captured record at phase-4 entry (requirement 1.8), and a throw from this
            // synchronous [RelayCommand] would escape ICommand.Execute after IsRunning was already
            // set. Deferring the capture strengthens that last point rather than weakening it -
            // the factory now runs inside RunAsync, where the catch list is.
            SubtaskConfiguration.Deferred(this),
            // Requirements 3.2 and 3.7: the run's own progress reaches the indicator through this
            // sink, so the count advances after each subtask and a failure's reason is readable
            // while the run continues. Until now the parameter defaulted to null and nothing in the
            // application ever supplied it.
            //
            // Constructed HERE, on the UI thread, exactly like the Progress<PhaseProgress> above:
            // Progress<T> captures the SynchronizationContext of the thread that creates it, so
            // this one captures the dispatcher's and posts every report onto its single-threaded
            // FIFO queue. Built on a pool thread instead it would have no context, fall back to
            // unordered ThreadPool.QueueUserWorkItem, and let requirement 3.7's ordered states
            // latch a stale payload - besides touching observable properties off the UI thread.
            new Progress<SubtaskProgress>(SubtaskIndicator.Apply));

        // Kept, not discarded: CompleteTaskAsync awaits this handle so the tab closes only after the
        // run has ended (requirement 5.3). Still not awaited HERE - StartWorkflow must return to the
        // dispatcher at once - so on every path except that await, RunAsync's own catch list is the
        // only error surface. On that one path it is not: see CompleteTaskAsync's remarks.
        _runTask = RunAsync(request, _run.Token);
    }

    // StartWorkflow deliberately does not await this task, so every failure it can produce has to
    // be caught HERE. Anything that escapes becomes an unobserved task exception while the UI
    // merely flips IsRunning back to false and says nothing - which contradicts every
    // "actionable message" row in spec section 12.1. The list below is exhaustive for the code
    // paths this design specifies; a catch-all is deliberately NOT added, because an unexpected
    // exception type should surface during development rather than be swallowed into a label.
    private async Task RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _orchestrator.RunAsync(request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Tab closed or application shutting down. Never reported as an error.
        }
        catch (PromptTemplateException ex)
        {
            ValidationMessage = ex.Message;
        }
        catch (SubtaskConfigurationException ex)
        {
            // Requirements 1.8 and 2.9: the tracking directory lost its marker, or decomposition
            // produced no usable index. User-fixable, and the German text is already on the
            // exception. The journal leaves Implementation Active, so the task stays recoverable -
            // which is why this must never be mistaken for a reason to fall back to a normal run.
            ValidationMessage = ex.ValidationMessage;
        }
        catch (ArtifactWatchException ex)
        {
            // The watched directory disappeared or the watcher failed (spec section 12.2).
            ValidationMessage = ex.Message;
        }
        catch (FileNotFoundException ex)
        {
            // ShellLocator found neither pwsh.exe nor powershell.exe.
            ValidationMessage = ex.Message;
        }
        catch (PlatformNotSupportedException ex)
        {
            // CreatePseudoConsole returned E_NOTIMPL - Windows older than 10 1809.
            ValidationMessage = ex.Message;
        }
        catch (Win32Exception ex)
        {
            // CreatePipe or CreateProcess failed.
            ValidationMessage = $"Die Terminal-Sitzung konnte nicht gestartet werden: {ex.Message}";
        }
        catch (IOException ex)
        {
            ValidationMessage = $"Dateisystemfehler waehrend des Workflows: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            ValidationMessage = $"Kein Zugriff auf das Arbeitsverzeichnis: {ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            // WebView2 initialisation, or Start called twice on one session.
            ValidationMessage = $"Das Terminal konnte nicht gestartet werden: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private void BrowseDirectory()
    {
        var chosen = _picker.PickDirectory(WorkingDirectory);
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            WorkingDirectory = chosen;
        }
    }

    /// <remarks>
    /// The caption is task 6.2's reason for the <c>title</c> parameter. The command's own
    /// <c>CanExecute</c> carries the phase-4 lock as well as the view's <c>IsEnabled</c> binding:
    /// <see cref="System.Windows.Controls.Primitives.ButtonBase"/> ands the two together, so the
    /// button is dead if either says so and the lock does not depend on one binding alone
    /// (requirement 1.6).
    /// </remarks>
    [RelayCommand(CanExecute = nameof(IsSubtaskConfigurationEditable))]
    private void BrowseWorkflowDirectory()
    {
        var chosen = _picker.PickDirectory(WorkflowDirectory, "Workflow-Verzeichnis auswählen");
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            WorkflowDirectory = chosen;
        }
    }

    private bool CanCompleteTask() => IsRunning && _activePhase == WorkflowPhase.Implementation;

    /// <summary>
    /// The <c>Task abschliessen</c> action: requirement 5.3 in its stated order - signal the run,
    /// await its end, record the explicit manual override - Implementation <c>Completed</c> plus
    /// <c>ImplementationCompletedManually</c>, both halves of design decision 6b - and only then
    /// ask for the tab to close.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The signal is the only thing that ends the run here, and it does end it: every session
    /// races its artefact watcher against this same <see cref="ManualPhaseSignal"/>, and the
    /// subtask loop re-asks the signal after each entry and returns when it has been raised. The
    /// signal is re-armed once per phase, never inside the loop, so it stays raised until the run
    /// is out. This stays the ONLY caller of <see cref="ManualPhaseSignal.Signal"/> in the
    /// application - that single-caller fact is what makes requirement 5.2 structural.
    /// </para>
    /// <para>
    /// The journal writes come AFTER the await and not before, which is both what requirement
    /// 5.3 lists and what keeps them safe: while the run is winding down the orchestrator still
    /// writes the journal itself - <c>SaveSubtaskSettings</c>, and
    /// <c>RecordPhase(Implementation, Completed)</c> if the final read happens to come back
    /// all-complete - and every one of those is a read-modify-write of the whole record from disk.
    /// Writing the override first would put it in a race with those; writing it last cannot lose.
    /// </para>
    /// <para>
    /// BOTH halves of design decision 6b are written here, and both are load-bearing. The flag
    /// alone does not satisfy requirement 5.4's recovery half: <c>PhaseReconciliation.Reconcile</c>
    /// only consults the evidence - and therefore only ever reads
    /// <c>ImplementationCompletedManually</c> - for an entry whose recorded status is already
    /// <see cref="PhaseStatus.Completed"/>, and <c>FirstIncomplete</c> hands an
    /// <see cref="PhaseStatus.Active"/> entry straight back as the phase to resume. In subtask mode
    /// the orchestrator deliberately records no completion of its own unless the final ledger read
    /// is all-complete, so this <c>RecordPhase</c> is the only writer of that half - recovery gets
    /// nothing for free from the recovery work of task 5.1. Requirement 5.4's presentation half,
    /// by contrast, does need nothing here: the indicator is driven by the counts alone, so a
    /// manual override leaves it showing what the ledger reported.
    /// </para>
    /// <para>
    /// What awaiting <c>_runTask</c> does and does not expose. It does NOT expose cancellation:
    /// <c>RunAsync</c> catches <see cref="OperationCanceledException"/> itself, so a cancelled run
    /// completes that task successfully and no such exception ever reaches this method. It DOES
    /// expose anything outside <c>RunAsync</c>'s catch list: without the await such a fault was
    /// merely an unobserved task exception, whereas the await re-throws it inside the generated
    /// <see cref="IAsyncRelayCommand"/>, which rethrows onto the UI thread - and the journal writes
    /// and the <see cref="Close"/> below are then skipped. That catch list is exhaustive for the
    /// paths this design specifies, so the case is believed unreachable; it is stated rather than
    /// called unchanged, because the await genuinely changed where such a fault would land.
    /// </para>
    /// <para>
    /// A second click while the first invocation is still awaiting is refused rather than queued.
    /// The generated <see cref="IAsyncRelayCommand"/> defaults to <c>AllowConcurrentExecutions =
    /// false</c>, so its <c>CanExecute</c> is false for as long as the execution task is pending
    /// and <see cref="System.Windows.Controls.Primitives.ButtonBase"/> - the only caller - keeps
    /// the button disabled. That guard sits on <c>CanExecute</c>, not on <c>Execute</c>, so it is
    /// the framework that enforces it; nothing here calls the command directly. Allowing
    /// concurrency instead would buy nothing - the signal is idempotent - and would cost a second
    /// <see cref="CloseRequested"/> for one user action.
    /// </para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanCompleteTask))]
    private async Task CompleteTaskAsync()
    {
        _manualSignal.Signal();

        var running = _runTask;
        if (running is not null)
        {
            await running;
        }

        // CanCompleteTask required IsRunning, so both are set; the guard mirrors
        // NotifyClosedByUser and keeps a composed path out of an impossible state.
        if (!string.IsNullOrWhiteSpace(WorkingDirectory) && !string.IsNullOrWhiteSpace(TaskName))
        {
            var paths = new TaskPaths(WorkingDirectory, TaskName);

            // RecordPhase FIRST, deliberately: it creates the journal when none exists, whereas
            // SetImplementationCompletedManually is a documented no-op without one. In the other
            // order the override could be dropped on the floor for a task whose journal never got
            // written. Two calls rather than one because RecordPhase touches a single phase entry
            // (ITaskStateStore.ReplacePhases remarks) and the flag lives outside the phase array;
            // each is its own whole-record read-modify-write, and the pair is not atomic. A crash
            // between them leaves Implementation Completed with the flag clear, which the next
            // reconciliation demotes off the unfinished ledger - the task is offered again, which
            // is the pre-override behaviour and never a false completion.
            _stateStore.RecordPhase(paths, WorkflowPhase.Implementation, PhaseStatus.Completed);
            _stateStore.SetImplementationCompletedManually(paths, completedManually: true);
        }

        // The same event the close button raises, so MainWindowViewModel's existing handling -
        // which dismisses a recovered task - applies unchanged (design "Presentation and Recovery").
        Close();
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
