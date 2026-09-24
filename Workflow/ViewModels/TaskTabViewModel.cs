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
/// the one snapshot the run carries. Nothing downstream ever holds the tab itself, so the enabled
/// flag and the tracking directory cannot be observed at two different moments (requirement 1.6).
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

        if (!string.IsNullOrWhiteSpace(settings.Settings.LastDirectory)
            && Directory.Exists(settings.Settings.LastDirectory))
        {
            WorkingDirectory = settings.Settings.LastDirectory;
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
        CompleteCurrentPhaseCommand.NotifyCanExecuteChanged();
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

    // Deliberately never Clear()s. TaskTabView.xaml binds this collection to the ComboBox's
    // ItemsSource while SelectedItem is bound TwoWay to WorkingDirectory, so emptying it makes the
    // Selector drop the selection and write null back into WorkingDirectory. Re-adding the entries
    // afterwards does not restore the selection: the box goes blank, validation reports a missing
    // working directory and 'Start workflow' dies the moment a second directory is picked.
    private void SyncRecentDirectories()
    {
        var desired = _settings.Settings.RecentDirectories;

        for (var i = RecentDirectories.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(RecentDirectories[i]))
            {
                RecentDirectories.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var at = RecentDirectories.IndexOf(desired[i]);

            if (at < 0)
            {
                RecentDirectories.Insert(i, desired[i]);
            }
            else if (at != i)
            {
                RecentDirectories.Move(at, i);
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
        && !string.IsNullOrWhiteSpace(TaskName)
        && !string.IsNullOrWhiteSpace(WorkingDirectory);

    [RelayCommand(CanExecute = nameof(CanStartWorkflow))]
    private void StartWorkflow()
    {
        SyncFolder();
        if (!CanStartWorkflow() || string.IsNullOrWhiteSpace(WorkingDirectory))
        {
            return;
        }

        IsNameLocked = true;
        IsRunning = true;

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
            ResumePhase ?? WorkflowPhase.Specification,
            // Requirement 4.2: without this a continued subtask task takes the normal
            // implementation branch and the skip-completed loop is never reached. Capture, not
            // CaptureValidated: the start gate checks the directory before the run begins and the
            // orchestrator re-validates the captured record at phase-4 entry, which is where
            // requirements 1.6 and 1.8 put the second check. Validating here instead would reject
            // a run whose phases 1-3 are still allowed to change the configuration, and would
            // throw out of a synchronous command handler.
            SubtaskConfiguration.Capture(this));

        _ = RunAsync(request, _run.Token);
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

    private bool CanCompleteCurrentPhase() => IsRunning && _activePhase is not null;

    [RelayCommand(CanExecute = nameof(CanCompleteCurrentPhase))]
    private void CompleteCurrentPhase() => _manualSignal.Signal();

    private bool CanCompleteTask() => IsRunning && _activePhase == WorkflowPhase.Implementation;

    [RelayCommand(CanExecute = nameof(CanCompleteTask))]
    private void CompleteTask() => _manualSignal.Signal();

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
