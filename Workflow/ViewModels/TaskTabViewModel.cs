using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;
using Workflow.Models;
using Workflow.Services;

namespace Workflow.ViewModels;

/// <summary>One Task, one tab: metadata, the phase indicators, and the terminal.</summary>
/// <remarks>
/// The tab is the live, editable subtask configuration of design issue 3: it implements
/// <see cref="ISubtaskConfiguration"/> so that <see cref="SubtaskConfiguration.Capture"/> can take
/// the one snapshot the run carries. The run is handed that capture <em>deferred</em>
/// (<see cref="SubtaskConfiguration.Deferred"/>), so it is taken at Implementation entry and the
/// user may still change the configuration while the phases before Implementation run; from that
/// moment on it is one immutable record. Nothing downstream ever holds the tab itself, so the enabled flag and the tracking
/// directory cannot be observed at two different moments (requirement 1.6).
/// </remarks>
public sealed partial class TaskTabViewModel : ObservableObject, ISubtaskConfiguration, IDisposable
{
    /// <summary>
    /// The info line of a finished task opened with <c>Task laden</c> (Workflow_LOAD_AND_PAUSE spec
    /// section 5.5): its green phase indicators stay, and this says what Start will do with it.
    /// </summary>
    private const string FinishedTaskMessage =
        "Alle Phasen sind abgeschlossen. 'Start workflow' beginnt den Task von vorn.";

    /// <summary>
    /// The info line of a task completed by hand with subtasks still open, opened with <c>Task laden</c>:
    /// every phase is done, but Start continues the implementation instead of restarting the task.
    /// </summary>
    private const string ReopenImplementationMessage =
        "Implementierung wurde per 'Task abschliessen' beendet, Subtasks sind noch offen. 'Continue workflow' setzt die Implementierung fort.";

    /// <summary>Asked before 'Start workflow' runs a finished task again from Specification.</summary>
    private const string RestartQuestion =
        "Task komplett von vorn beginnen? Spezifikation und alle Phasen werden neu ausgeführt.";

    /// <summary>
    /// The info line when <c>Implementierung beginnen</c> could not record its skips (Workflow_LoadFix
    /// spec 8.3, D19): nothing was started, because Implementation must never run on a journal that
    /// does not say the phases before it were skipped.
    /// </summary>
    private const string SkipNotRecordedMessage =
        "Implementierung nicht gestartet: Das Überspringen der Review konnte nicht in .workflow-state.json gespeichert werden.";

    /// <summary>The phases <c>Implementierung beginnen</c> skips (Workflow_LoadFix spec 8.3).</summary>
    private static readonly WorkflowPhase[] SkippablePhases =
        [WorkflowPhase.Simplifier, WorkflowPhase.Review, WorkflowPhase.ResolveReview];

    private readonly ITaskFolderService _folders;
    private readonly IWorkflowOrchestrator _orchestrator;
    private readonly ISettingsService _settings;
    private readonly ITaskStateStore _stateStore;
    private readonly IDirectoryPickerService _picker;
    private readonly IMessageDialogService? _messages;
    private readonly ManualPhaseSignal _manualSignal = new();

    // The tab's pause switch (Workflow_LOAD_AND_PAUSE spec 6.4). One per tab for its whole lifetime,
    // so a pause armed before 'Start workflow', or left armed when a run ends, reaches the next run.
    private readonly PhasePauseGate _pauseGate = new();

    // The tab's 'Desktop-Job jetzt starten' signal (WF_DESKTOPJOBBUTTON spec 4.6). One per tab for its
    // whole lifetime, like the pause gate, so the button always talks to the signal the current run holds.
    private readonly DesktopJobStartSignal _desktopJobStart = new();
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

    // Workflow_LoadFix spec 8.3 (D13): true while 'Implementierung beginnen' is between cancelling the
    // running phase and starting Implementation. CanStartWorkflow reads it, because the cancelled run's
    // finally drops IsRunning before the new run exists.
    private bool _isSwitchingToImplementation;

    // 'Task laden' handed over a task completed by hand with subtasks still open: every phase is done,
    // yet Start must continue the implementation rather than restart (it routes to
    // StartImplementationAsync, which clears the flag). Cached so the label getter never reads the disk.
    private bool _reopensImplementation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    [NotifyCanExecuteChangedFor(nameof(StartWorkflowCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartImplementationCommand))]
    private string _taskName = string.Empty;

    [ObservableProperty]
    private string _taskDescription = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartWorkflowCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartImplementationCommand))]
    private string? _workingDirectory;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartWorkflowCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartImplementationCommand))]
    [NotifyPropertyChangedFor(nameof(IsQueueEditable))]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isNameLocked;

    /// <summary>
    /// The open tab whose finish starts this tab's workflow by itself, or <see langword="null"/> when
    /// nothing is queued (WF_QUEUE). Not persisted: it lives and dies with the tab. Any start of this
    /// tab, by click or by the queue, clears it (spec Q6).
    /// </summary>
    [ObservableProperty]
    private TaskTabViewModel? _queuedAfter;

    /// <summary>
    /// True while this tab is the one shown. The tab template keeps every tab's view alive and
    /// toggles visibility on this flag, so each tab owns its WebView2 for its whole lifetime.
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartWorkflowCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartImplementationCommand))]
    private string? _validationMessage;

    [ObservableProperty]
    private string? _infoMessage;

    /// <summary>
    /// Why the running session is not sending its prompt: the launcher is not up, and the terminal
    /// is waiting for the user to start one by hand.
    /// </summary>
    /// <remarks>
    /// Its own property rather than a second writer of <see cref="InfoMessage"/>. That line already
    /// carries independent facts - a reused task folder, a recovered run - and a launcher stall must
    /// neither overwrite them nor erase them when it clears. Like <see cref="InfoMessage"/> it is
    /// deliberately NOT part of <see cref="CanStartWorkflow"/>: it annotates a run in progress and
    /// must never disable an action.
    /// </remarks>
    [ObservableProperty]
    private string? _launcherStatusMessage;

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
    [NotifyCanExecuteChangedFor(nameof(StartImplementationCommand))]
    private string? _workflowDirectoryMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartButtonLabel))]
    private WorkflowPhase? _resumePhase;

    [ObservableProperty]
    private bool _isRecovered;

    /// <summary>
    /// Whether this task's implementation phase runs as subtasks (requirement 1.1). Editable while
    /// the phases before Implementation run; read exactly once, at capture time.
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

    /// <summary>
    /// True while the user has paused this tab: the running phase finishes, and the next phase
    /// starts only after Play (Workflow_LOAD_AND_PAUSE spec section 6). Not persisted.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(UnpauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartDesktopJobNowCommand))]
    private bool _isPaused;

    /// <summary>
    /// True while a run waits for the desktop to become idle before starting a desktop job
    /// (WF_DESKTOPJOBBUTTON spec 4.6). Mirrors the tab's start signal; not persisted.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartDesktopJobNowCommand))]
    private bool _isDesktopJobWaiting;

    /// <summary>
    /// True once a run on this tab has ended with every phase completed - Implementation wrote its done
    /// file. The Start button then gives way to <c>Task abschliessen</c>, which closes the tab:
    /// there is nothing left to continue. Never cleared, because the name lock leaves this tab
    /// bound to the finished task; restarting it is what <c>Task laden</c> is for.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartWorkflowCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartImplementationCommand))]
    private bool _isTaskFinished;

    /// <summary>
    /// Number of gate items this task left for a human - from the done file and from every completed
    /// subtask's <c>status.json</c> - read errors included. Drives the icon beside the phases.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenGateItems))]
    private int _openGateItemCount;

    /// <summary>True when at least one source of open gate items could not be read.</summary>
    [ObservableProperty]
    private bool _hasOpenGateReadErrors;

    /// <summary>The open gate items as copyable text, grouped by source; empty when there are none.</summary>
    [ObservableProperty]
    private string _openGateItemsText = string.Empty;

    /// <summary>True when the open-items icon is shown.</summary>
    public bool HasOpenGateItems => OpenGateItemCount > 0;

    /// <summary>Creates the tab.</summary>
    /// <param name="folders">Task-folder service.</param>
    /// <param name="orchestrator">The phase state machine.</param>
    /// <param name="settings">Shared settings, used for the directory MRU.</param>
    /// <param name="stateStore">The per-task workflow journal, read to re-arm Continue.</param>
    /// <param name="picker">Folder-browser dialog.</param>
    /// <param name="terminal">This tab's terminal.</param>
    /// <param name="folderDebounce">Delay before a name change touches the disk.</param>
    /// <param name="startupErrors">
    /// Prompt-template problems found at startup. While this list is non-empty
    /// <c>StartWorkflowCommand</c> cannot execute on this tab (F17, spec section 9.4).
    /// </param>
    /// <param name="messages">
    /// Asks the two safety questions: completing a task with open subtasks, and restarting a
    /// finished task from the specification. Null asks nothing and proceeds, as before the
    /// questions existed; the application's factory always supplies one.
    /// </param>
    public TaskTabViewModel(
        ITaskFolderService folders,
        IWorkflowOrchestrator orchestrator,
        ISettingsService settings,
        ITaskStateStore stateStore,
        IDirectoryPickerService picker,
        TerminalViewModel terminal,
        TimeSpan folderDebounce,
        IReadOnlyList<string> startupErrors,
        IMessageDialogService? messages = null)
    {
        ArgumentNullException.ThrowIfNull(startupErrors);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(stateStore);

        _folders = folders;
        _orchestrator = orchestrator;
        _settings = settings;
        _stateStore = stateStore;
        _picker = picker;
        _messages = messages;
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

        // 'Implementierung beginnen' depends on the Spezifikation and Implementierung indicators,
        // which ApplyJournal, ApplyProgress, ResetPhaseIndicators and the skip all write. Listening
        // here covers every writer at once; the tab owns the indicators for its whole lifetime, so
        // the handlers never outlive them (Workflow_LoadFix spec 8.2).
        foreach (var indicator in Phases)
        {
            indicator.PropertyChanged += (_, _) => StartImplementationCommand.NotifyCanExecuteChanged();
        }

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

        // A run pauses the gate itself while it holds for the approval of its decisions (B7); the
        // property follows, so Play appears. Posted when raised off the creating thread, because the
        // property drives command state. The gate lives exactly as long as this tab, so the handler
        // never outlives it.
        var ui = SynchronizationContext.Current;
        _pauseGate.Paused += (_, _) =>
        {
            if (ui is null || SynchronizationContext.Current == ui)
            {
                IsPaused = true;
            }
            else
            {
                ui.Post(_ => IsPaused = true, null);
            }
        };

        // Same threading rule as the pause gate; the value is re-read on delivery so a posted update
        // never applies a stale state.
        _desktopJobStart.WaitingChanged += (_, _) =>
        {
            if (ui is null || SynchronizationContext.Current == ui)
            {
                IsDesktopJobWaiting = _desktopJobStart.IsWaiting;
            }
            else
            {
                ui.Post(_ => IsDesktopJobWaiting = _desktopJobStart.IsWaiting, null);
            }
        };
    }

    /// <summary>Raised when the tab's close button is clicked.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// Raised when the task is finished (WF_QUEUE spec Q7): (a) at the end of a run that left every phase
    /// completed, after <see cref="IsRunning"/> dropped, and (b) when 'Task abschliessen' completes the task,
    /// before the tab closes. No one-shot guard: a second raise finds nothing queued after this tab.
    /// </summary>
    public event EventHandler? Finished;

    /// <summary>The tab header: the task name, or '(Bezeichnung)' while it is empty.</summary>
    public string Header => string.IsNullOrWhiteSpace(TaskName) ? "(Bezeichnung)" : TaskName.Trim();

    /// <summary>
    /// The Start button's caption: 'Continue workflow' once a resume point is known, or when a task
    /// completed by hand with subtasks still open is continued at the implementation.
    /// </summary>
    public string StartButtonLabel =>
        ResumePhase is null && !_reopensImplementation ? "Start workflow" : "Continue workflow";

    /// <summary>
    /// The open tabs this tab can queue after (WF_QUEUE). The shell keeps the list in step with the open
    /// tabs; the Queue box binds to it.
    /// </summary>
    public ObservableCollection<TaskTabViewModel> QueueCandidates { get; } = [];

    /// <summary>Whether the Queue box accepts a pick: it is locked while the tab's workflow runs (WF_QUEUE spec Q5).</summary>
    public bool IsQueueEditable => !IsRunning;

    /// <summary>
    /// Starts this tab's workflow when it is queued after <paramref name="finished"/> (WF_QUEUE spec Q9).
    /// The queue is cleared first - the predecessor cannot finish a second time - and the start goes through
    /// <see cref="StartWorkflowCommand"/>, the same call a click makes, with every gate.
    /// </summary>
    /// <param name="finished">The tab that just finished.</param>
    /// <returns><see langword="true"/> when a run began; <see langword="false"/> when this tab was not queued
    /// after <paramref name="finished"/> or the start was refused (an info line says so).</returns>
    public bool StartIfQueuedAfter(TaskTabViewModel finished)
    {
        ArgumentNullException.ThrowIfNull(finished);

        if (!ReferenceEquals(QueuedAfter, finished))
        {
            return false;
        }

        QueuedAfter = null;

        if (StartWorkflowCommand.CanExecute(null))
        {
            StartWorkflowCommand.Execute(null);
        }

        // Read AFTER the call: CanExecute is not evidence of a start, StartWorkflow can still refuse
        // (restart question answered 'Nein', re-validated directory, unsaved description).
        var started = IsRunning;
        if (!started)
        {
            // Set after the call, so StartWorkflow's own InfoMessage writes cannot hide it.
            InfoMessage = $"Queue: '{finished.Header}' ist fertig, der Workflow konnte aber nicht automatisch gestartet werden.";
        }

        return started;
    }

    /// <summary>
    /// Re-queues this tab behind <paramref name="next"/> when it was queued after <paramref name="finished"/>:
    /// both want the same working directory, so they run one after the other (WF_QUEUE spec Q10).
    /// </summary>
    /// <param name="finished">The tab that just finished.</param>
    /// <param name="next">The tab that was started in its place and still runs.</param>
    public void MoveQueueAfter(TaskTabViewModel finished, TaskTabViewModel next)
    {
        ArgumentNullException.ThrowIfNull(finished);
        ArgumentNullException.ThrowIfNull(next);

        if (!ReferenceEquals(QueuedAfter, finished))
        {
            return;
        }

        QueuedAfter = next;
        InfoMessage = $"Queue: '{finished.Header}' ist fertig; wartet jetzt auf '{next.Header}' (gleiches Arbeitsverzeichnis).";
    }

    /// <summary>
    /// Drops this tab's queue when it was queued after <paramref name="closed"/>, which the user closed
    /// without finishing (WF_QUEUE spec Q11).
    /// </summary>
    /// <param name="closed">The tab that was closed.</param>
    public void CancelIfQueuedAfter(TaskTabViewModel closed)
    {
        ArgumentNullException.ThrowIfNull(closed);

        if (!ReferenceEquals(QueuedAfter, closed))
        {
            return;
        }

        QueuedAfter = null;
        InfoMessage = $"Queue aufgehoben: '{closed.Header}' wurde geschlossen.";
    }

    /// <summary>The station indicators, one per phase.</summary>
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
    /// False while Implementation is running, which is when requirement 1.6 freezes the subtask
    /// configuration: the checkbox, the tracking-directory selector and the folder-picker button.
    /// </summary>
    /// <remarks>
    /// Derived from the same <c>_activePhase</c> field the task-completion commands read, so
    /// <see cref="ApplyProgress"/> - the only writer - raises the change notification for it. The
    /// phases before Implementation deliberately leave the configuration editable: the snapshot the
    /// run carries is taken at Implementation entry, not at run entry, so an edit made while they
    /// run still reaches the run.
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

    /// <summary>Applies one launcher-status report from the running workflow.</summary>
    /// <param name="message">
    /// Why the session is not sending its prompt, or null once it no longer applies.
    /// </param>
    /// <remarks>
    /// A named handler rather than a lambda inside the sink, for the same reason
    /// <see cref="ApplyProgress"/> is one: a test that drove this through
    /// <see cref="Progress{T}"/> would be exercising the dispatcher rather than this behaviour.
    /// It writes only its own property, so an unrelated <see cref="InfoMessage"/> survives both the
    /// report and the clear.
    /// </remarks>
    public void ApplyLauncherStatus(string? message) => LauncherStatusMessage = message;

    /// <summary>Applies a phase status change from the orchestrator. Public for testing.</summary>
    /// <param name="progress">The reported change.</param>
    public void ApplyProgress(PhaseProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var indicator = Phases.First(p => p.Phase == progress.Phase);
        indicator.Status = progress.Status;

        _activePhase = progress.Status == PhaseStatus.Active ? progress.Phase : null;

        CompleteTaskCommand.NotifyCanExecuteChanged();

        // _activePhase is set after the indicator, so the indicator's own notification above saw the
        // old value.
        StartImplementationCommand.NotifyCanExecuteChanged();

        // _activePhase is a plain field, so nothing else announces the editing lock. Without these
        // two lines the XAML IsEnabled bindings would be evaluated once and never again, and the
        // Implementation lock of requirement 1.6 would exist only inside this class.
        OnPropertyChanged(nameof(IsSubtaskConfigurationEditable));
        BrowseWorkflowDirectoryCommand.NotifyCanExecuteChanged();

        // Implementation completes exactly when the done file exists or every subtask is complete,
        // which is when the open gate items are written.
        if (progress is { Phase: WorkflowPhase.Implementation, Status: PhaseStatus.Completed })
        {
            RefreshOpenGateItems();
        }
    }

    /// <summary>
    /// Prefills this tab from an interrupted task found by the startup scan, or from a task folder
    /// picked with <c>Task laden</c> - which may be a finished one.
    /// </summary>
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
            RefreshOpenGateItems();
            ResumePhase = task.ResumePhase;
            IsRecovered = true;

            // Only 'Task laden' hands over a finished task; the startup scan never does.
            if (task.ResumePhase is not { } resume)
            {
                // Reconciliation keeps a manual completion complete whatever the ledger says
                // (requirement 5.4), so this task looks finished - but it has work left.
                SetReopensImplementation(IsCompletedManuallyWithOpenSubtasks());
                InfoMessage = _reopensImplementation ? ReopenImplementationMessage : FinishedTaskMessage;
            }
            else if (task.JournalCreated)
            {
                // Workflow_LoadFix spec 5.4: the folder had no journal. Say that one was written and
                // where the run picks up - the indicators alone look like any other recovered task.
                InfoMessage = $"Neue .workflow-state.json angelegt. Nächste Phase: {PhaseCatalog.For(resume).DisplayName}.";
            }
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
        var snapshot = TryReadLedger(taskName);

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
            snapshot.States,
            snapshot.Waiting,
            snapshot.Deferred));
    }

    // The ledger this tab's own subtask configuration names - while Implementation runs, the one the run
    // captured, because the selectors are locked then. Null for every unusable case: mode off, no
    // directory, a blank or malformed path, or an index that cannot be read.
    private SubtaskSnapshot? TryReadLedger(string taskName) =>
        TryTrackingPaths(taskName) is { } tracking ? SubtaskLedger.TryRead(tracking) : null;

    // Null for mode off, no directory, or a blank or malformed path.
    private SubtaskPaths? TryTrackingPaths(string taskName)
    {
        if (!SubtasksEnabled || WorkflowDirectory is null)
        {
            return null;
        }

        try
        {
            return new SubtaskPaths(WorkflowDirectory, taskName);
        }
        catch (ArgumentException)
        {
            // The journal stores the chosen directory verbatim, so a blank or whitespace-only
            // value survives to here by design; it is a user condition, not a defect.
            return null;
        }
    }

    // Synchronous on the dispatcher, like SeedSubtaskIndicatorFromLedger: a few small files, and no
    // late result that could land on a tab whose task identity has changed in the meantime.
    private void RefreshOpenGateItems()
    {
        var report = ReadOpenGateItems();

        OpenGateItemCount = report.Count;
        HasOpenGateReadErrors = report.HasReadErrors;
        OpenGateItemsText = OpenGateItems.Format(TaskName, report);
    }

    private OpenGateReport ReadOpenGateItems()
    {
        if (string.IsNullOrWhiteSpace(WorkingDirectory) || !_folders.Validate(TaskName, WorkingDirectory).IsValid)
        {
            return OpenGateReport.Empty;
        }

        var paths = new TaskPaths(WorkingDirectory, TaskName);
        var tracking = TryTrackingPaths(paths.TaskName);

        return OpenGateItems.Combine(
            OpenGateItems.FromDoneFile(paths.DoneAbsolute),
            tracking is null ? OpenGateReport.Empty : OpenGateItems.FromSubtasks(tracking));
    }

    /// <summary>
    /// Re-reads the open gate items when their overview opens, so files changed while the tab was
    /// open are shown as they are now and a read error gets its retry.
    /// </summary>
    [RelayCommand]
    private void RefreshOpenGateItemsOnOpen() => RefreshOpenGateItems();

    // The run's subtask sink: the indicator first, then the items a just-completed subtask left open.
    private void ApplySubtaskProgress(SubtaskProgress progress)
    {
        SubtaskIndicator.Apply(progress);
        RefreshOpenGateItems();
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

        // The same reconciliation the startup scan applies, from the same helper: an unreconciled
        // re-arm would resume into a phase whose inputs have been deleted, or re-run one whose
        // outputs already exist (SPEC section 6.3.2). Without a journal there is nothing to
        // reconcile, and the tab below stays a fresh start even if the folder holds artefacts.
        if (state is not null && PhaseReconciliation.Reconcile(paths, state))
        {
            _stateStore.ReplacePhases(paths, [.. state.Phases]);
        }

        var next = state is null ? null : PhaseReconciliation.FirstIncomplete(state);

        if (state is null || next is null)
        {
            // No journal, or a finished task: this tab is a fresh start and must look like one.
            // Leaving the indicators alone would keep painting the PREVIOUSLY typed task's green
            // phases onto this one, or show every phase green above a 'Start workflow' button.
            ResetPhaseIndicators();
            ResumePhase = null;
            return;
        }

        ApplyJournal(state);
        ResumePhase = next;
    }

    // From the given phase on, never before it: the phases before a resumed run's start phase are
    // the only record that they happened - the same split ClearPhasesFrom makes in the journal.
    private void ResetPhaseIndicators(WorkflowPhase from = WorkflowPhase.Specification)
    {
        foreach (var indicator in Phases)
        {
            if (indicator.Phase >= from)
            {
                indicator.Status = PhaseStatus.Pending;
            }
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

        // The subtask half of the open gate items exists only in subtask mode.
        RefreshOpenGateItems();
    }

    partial void OnWorkflowDirectoryChanged(string? value)
    {
        // The tracking directory is where the subtask half of the open gate items lives. Not
        // IsNameLocked-guarded: a finished tab may still be pointed at its tracking directory.
        RefreshOpenGateItems();

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

    // Every task-identity change ends here (name, working directory, Start), so the open gate items
    // are re-read for the task the tab now names - or cleared when it names none.
    private void SyncFolder()
    {
        SyncFolderCore();
        RefreshOpenGateItems();
    }

    private void SyncFolderCore()
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
        && !IsTaskFinished
        // Workflow_LoadFix spec 8.3 (D13): shut while 'Implementierung beginnen' replaces the run.
        && !_isSwitchingToImplementation
        && StartGatesOpen();

    // What both start actions require besides "no run is going" (Workflow_LoadFix spec 8.2).
    private bool StartGatesOpen() =>
        // F17 / spec section 9.4: a prompt-template problem disables Start on EVERY tab,
        // including tabs opened with '+' after the startup dialog was dismissed. Holding the
        // flag only on MainWindowViewModel leaves the button executable once the modal is gone.
        _startupErrors.Count == 0
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

        // A task completed by hand with subtasks still open is continued, not restarted: the same
        // switch 'Implementierung beginnen' makes, which clears the flag and calls back in here with
        // ResumePhase set. Re-checked against the journal, because the cached field is only a hint.
        if (ResumePhase is null && _reopensImplementation)
        {
            if (!IsCompletedManuallyWithOpenSubtasks())
            {
                // The disk changed since loading (subtasks finished, flag cleared). The click asked
                // for a continuation, so it must not become a restart: start nothing and let the
                // button and the info line say what Start does now.
                SetReopensImplementation(false);
                InfoMessage = IsEveryPhaseCompleted(new TaskPaths(WorkingDirectory, TaskName)) ? FinishedTaskMessage : null;
                return;
            }

            if (StartImplementationCommand.CanExecute(null))
            {
                StartImplementationCommand.Execute(null);
            }

            return;
        }

        // A finished task - nothing to resume, every phase done - would be run again from Specification,
        // and the orchestrator's first act is to clear the whole journal. Asked here, after the
        // start gate and before anything is written, so 'Nein' leaves the task exactly as it was.
        if (ResumePhase is null
            && IsEveryPhaseCompleted(new TaskPaths(WorkingDirectory, TaskName))
            && !Confirm(RestartQuestion))
        {
            return;
        }

        var paths = new TaskPaths(WorkingDirectory, TaskName);

        // Persist the description BEFORE the run: a crash one second from now must still recover
        // a tab with its Taskbeschreibung intact. Also before IsRunning, so a failed save refuses the
        // start instead of leaving a "running" tab with no run (WF_QUEUE spec Q14).
        if (!TrySaveDescription(paths))
        {
            return;
        }

        // Any start uses up the tab's own queue (WF_QUEUE spec Q6); a refused start returned above and keeps it.
        QueuedAfter = null;

        IsNameLocked = true;
        IsRunning = true;

        var startPhase = ResumePhase ?? WorkflowPhase.Specification;

        // The orchestrator's first act is to clear the journal from the start phase on
        // (ClearPhasesFrom); the indicators follow suit here, after the start gate, so a refused
        // start destroys nothing. Without it a restarted finished task - which 'Task laden' can
        // open - would show the phases after Specification green while Specification runs.
        ResetPhaseIndicators(startPhase);

        // Requirement 3.4: the subtask indicator is grey before Implementation, and it cannot be
        // grey "with counts" - a row reading '3 von 6' while Specification runs describes work this
        // run has not looked at. Anything the row was showing therefore goes now, AFTER the start gate, so
        // a refused start never destroys what the user is reading while they fix it.
        //
        // The one exception is a run that starts AT Implementation. Its counts are not a previous run's
        // leftovers: LoadForResume read them off the tracking files moments ago (requirement 4.5)
        // and they are exactly the state this run continues from, so the grey-before-Implementation rule
        // has nothing left to say about them. Resetting there would make the tab claim '0 von 0'
        // work done - and keep claiming it if the run failed before publishing anything, which the
        // Implementation-entry configuration check can do (requirement 1.8).
        if (startPhase != WorkflowPhase.Implementation)
        {
            SubtaskIndicator.Reset();
        }

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
            // Deferred, not Capture: requirement 1.6 puts the snapshot at Implementation entry, and this
            // is run entry. The two coincide only for a resumed task; for a run started at Specification
            // the user may still tick the checkbox or pick a directory while the phases before
            // Implementation run, and
            // capturing here would silently drop that edit. What travels is therefore a once-only
            // capture the orchestrator forces when it enters Implementation - still a snapshot from that
            // moment on, still nothing the orchestrator can re-read.
            //
            // Deferred, not a validating variant, for the same two reasons as before: the start
            // gate checks the directory before the run begins, the orchestrator re-validates the
            // captured record at Implementation entry (requirement 1.8), and a throw from this
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
            new Progress<SubtaskProgress>(ApplySubtaskProgress),
            // Created on the UI thread for the same reason as the two sinks above: Progress<T>
            // captures the creating thread's SynchronizationContext, and only the dispatcher's
            // gives ordered delivery onto observable properties. Without this argument the
            // orchestrator's stall reason would exist but reach nothing, and the refusal to paste
            // would be visible only as a terminal that appears to have stopped for no reason.
            new Progress<string?>(ApplyLauncherStatus),
            // Spec 6.3: the run waits at a phase boundary while this tab is paused. The tab's own
            // gate rather than a fresh one, so a pause armed before Start still counts.
            _pauseGate,
            // WF_DESKTOPJOBBUTTON spec 4.6: the tab's own signal, so the button reaches this run's wait.
            _desktopJobStart);

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

            // Only a run that came back normally can have finished the task.
            IsTaskFinished = IsEveryPhaseCompleted(request.Paths);
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

            // The orchestrator clears this itself on every ordinary exit, but a run that ends by
            // exception unwinds past that point. A stall reason outliving its run would tell the
            // user a stopped terminal is still waiting for a launcher.
            LauncherStatusMessage = null;
        }

        // WF_QUEUE spec Q7 (a): after the finally, so the tab is settled (IsRunning false) when the handlers
        // run and a queued tab's start problem is never caught by this run's catch list above.
        if (IsTaskFinished)
        {
            Finished?.Invoke(this, EventArgs.Empty);
        }
    }

    // False when the journal write failed; the message says why. Only these two exception types are
    // known to come out of the synchronous part of StartWorkflow, and no catch-all is added (WF_QUEUE spec Q14).
    private bool TrySaveDescription(TaskPaths paths)
    {
        try
        {
            _stateStore.SaveDescription(paths, TaskDescription);
            return true;
        }
        catch (IOException ex)
        {
            ValidationMessage = $"Die Taskbeschreibung konnte nicht gespeichert werden: {ex.Message}";
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            ValidationMessage = $"Kein Zugriff auf das Arbeitsverzeichnis: {ex.Message}";
            return false;
        }
    }

    // The journal rather than the indicators: the orchestrator wrote it before returning, whereas
    // the indicators are fed through Progress<T>, whose delivery this method does not control. A
    // subtask run that stopped short leaves Implementation Active, so it never counts as finished.
    private bool IsEveryPhaseCompleted(TaskPaths paths) =>
        _stateStore.TryLoad(paths) is { } state && PhaseReconciliation.FirstIncomplete(state) is null;

    // Without a dialog service nothing is asked and the action proceeds, as before the questions.
    private bool Confirm(string question) => _messages?.Confirm(question) ?? true;

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
    /// <c>CanExecute</c> carries the Implementation lock as well as the view's <c>IsEnabled</c> binding:
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

    // The property is the gate's only writer, so the buttons and the gate cannot disagree.
    partial void OnIsPausedChanged(bool value)
    {
        if (value)
        {
            _pauseGate.Pause();
        }
        else
        {
            _pauseGate.Unpause();
        }
    }

    private bool CanPause() => !IsPaused;

    /// <remarks>
    /// Deliberately independent of <see cref="IsRunning"/> and of the active phase, and it never
    /// touches <c>_manualSignal</c>: pausing only withholds the NEXT phase, or in Implementation the
    /// next subtask, so it is as safe to press before a run as during one (Workflow_LOAD_AND_PAUSE spec
    /// section 6.6).
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause() => IsPaused = true;

    private bool CanUnpause() => IsPaused;

    [RelayCommand(CanExecute = nameof(CanUnpause))]
    private void Unpause() => IsPaused = false;

    private bool CanStartDesktopJobNow() => IsDesktopJobWaiting && !IsPaused;

    /// <remarks>
    /// A press outside a wait is dropped by the signal itself (WF_DESKTOPJOBBUTTON spec A1), so a
    /// stale click can never arm a later wait.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanStartDesktopJobNow))]
    private void StartDesktopJobNow() => _desktopJobStart.Request();

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
        // Completing with open subtasks is final - recovery never offers the task again - while
        // stopping is what Pause and closing the tab are for. 'Nein' signals, writes and closes
        // nothing. No readable index, or every entry complete: nothing to warn about.
        if (TryReadLedger(TaskName) is { } ledger
            && ledger.Completed < ledger.Total
            && !Confirm(
                $"Task als abgeschlossen markieren? {ledger.Total - ledger.Completed} von {ledger.Total} Subtasks sind nicht complete. "
                + "Zum Anhalten Pause verwenden oder den Tab schließen."))
        {
            return;
        }

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
        //
        // Finished first (WF_QUEUE spec Q7 b, Q12): the tabs queued after this one start before the tab goes.
        // The journal writes above are durable, so a successor's failure must never keep this tab open - the
        // exception still surfaces through the command. A second raise is harmless (spec Q7).
        try
        {
            Finished?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            Close();
        }
    }

    private bool CanStartImplementation() =>
        !IsTaskFinished
        && StartGatesOpen()
        // The implementation reads the spec and the plan: Specification must be done...
        && StatusOf(WorkflowPhase.Specification) == PhaseStatus.Completed
        // ...and Implementation neither done nor running - a running one is Task abschliessen's turn.
        // Done is the one exception: a 'Task abschliessen' with subtasks still open is reopened.
        && (StatusOf(WorkflowPhase.Implementation) != PhaseStatus.Completed || IsCompletedManuallyWithOpenSubtasks())
        && _activePhase != WorkflowPhase.Implementation;

    private PhaseStatus StatusOf(WorkflowPhase phase) => Phases.First(p => p.Phase == phase).Status;

    private void SetReopensImplementation(bool value)
    {
        if (_reopensImplementation != value)
        {
            _reopensImplementation = value;
            OnPropertyChanged(nameof(StartButtonLabel));
        }
    }

    // The journal says Implementation was completed by hand, and the index it names - composed exactly as
    // reconciliation composes it - is readable and still has entries that are not complete. An
    // unreadable index says nothing is left (requirement 4.6), and an Implementation that completed on
    // its own is never reopened.
    private bool IsCompletedManuallyWithOpenSubtasks()
    {
        if (string.IsNullOrWhiteSpace(WorkingDirectory) || string.IsNullOrWhiteSpace(TaskName))
        {
            return false;
        }

        var paths = new TaskPaths(WorkingDirectory, TaskName);

        return _stateStore.TryLoad(paths) is { ImplementationCompletedManually: true, SubtasksEnabled: true } state
            && PhaseReconciliation.TryTrackingPaths(paths, state) is { } tracking
            && SubtaskLedger.TryRead(tracking) is { } snapshot
            && snapshot.Completed < snapshot.Total;
    }

    /// <summary>
    /// <c>Implementierung beginnen</c> (Workflow_LoadFix spec section 8): ends a running run by
    /// cancelling it, re-reconciles the journal with the disk, records Vereinfachen, Review and
    /// Review umsetzen as skipped where they are not completed in one verified write, and starts a run
    /// at Implementation - in subtask mode when the checkbox says so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cancellation, not <see cref="ManualPhaseSignal"/>: the signal would record the running review
    /// as Completed - a review that never happened - the run would go on into Review umsetzen, and
    /// <c>Task abschliessen</c> must stay the signal's only caller (subtask requirement 5.2).
    /// Cancelling is the path closing the tab already takes: the orchestrator disposes the session in
    /// its finally, and <c>RunAsync</c> swallows the <see cref="OperationCanceledException"/>.
    /// </para>
    /// <para>
    /// The journal is written only after the cancelled run has ended, for the reason
    /// <see cref="CompleteTaskAsync"/> gives: until then the orchestrator still writes whole-record
    /// read-modify-writes, and a late <c>RecordPhase(ResolveReview, Active)</c> would overwrite the
    /// skip.
    /// </para>
    /// <para>
    /// Nothing is started when the tab was closed while the run wound down, when the journal cannot
    /// be read, when Specification is no longer backed by a spec and a plan on disk, or when the skip did
    /// not land; in the last three cases the info line says why.
    /// </para>
    /// </remarks>
    /// <returns>A task that completes once Implementation was started or the switch was refused.</returns>
    [RelayCommand(CanExecute = nameof(CanStartImplementation))]
    private async Task StartImplementationAsync()
    {
        _isSwitchingToImplementation = true;
        StartWorkflowCommand.NotifyCanExecuteChanged();

        try
        {
            var running = _runTask;
            if (IsRunning && running is not null)
            {
                // Cancel, not CancelAsync: the same synchronous cancel Dispose issues, so the run's
                // callbacks unwind on the dispatcher exactly as when the tab is closed (spec 8.3 step 2).
#pragma warning disable CA1849
                _run?.Cancel();
#pragma warning restore CA1849
                await running;
            }

            // Closing the tab while the run wound down disposed the terminal: nothing may start now.
            if (_disposed != 0 || string.IsNullOrWhiteSpace(WorkingDirectory) || string.IsNullOrWhiteSpace(TaskName))
            {
                return;
            }

            var paths = new TaskPaths(WorkingDirectory, TaskName);

            // Reopening a task completed by hand with subtasks still open: the flag goes FIRST, so
            // the reconciliation inside the skip step demotes Implementation off the unfinished
            // ledger (PhaseReconciliation.Demote) and the journal resumes at Implementation - the
            // phases before it keep their entries, and the orchestrator clears only from
            // Implementation on.
            if (IsCompletedManuallyWithOpenSubtasks())
            {
                _stateStore.SetImplementationCompletedManually(paths, completedManually: false);
            }

            SetReopensImplementation(false);

            if (!TryRecordReviewSkips(paths))
            {
                // Nothing is skipped beyond what landed and no Implementation request is ever built; the
                // indicators show the journal, so the tab and the disk agree.
                return;
            }

            ResumePhase = WorkflowPhase.Implementation;
        }
        finally
        {
            _isSwitchingToImplementation = false;
            StartWorkflowCommand.NotifyCanExecuteChanged();
        }

        // The same start every other run takes: the folder re-check, the tracking-directory
        // re-validation, the description save and the deferred subtask capture (spec 8.3 step 8).
        // A non-recovered tab re-derives the same Implementation start from the journal, which
        // holds the skips.
        StartWorkflow();
    }

    // Spec 8.3 steps 4 and 5. False means nothing may start, and InfoMessage says why.
    private bool TryRecordReviewSkips(TaskPaths paths)
    {
        // Step 4 (D14, D18): the indicators are what LoadForResume left behind, and the disk may have
        // changed since - spec or plan deleted or emptied. A recovered tab never re-reconciles on its
        // own (RefreshResumeStateFromJournal returns at once for IsRecovered), so this does it for
        // both tab kinds, with the same helper and the same persistence.
        var journal = _stateStore.TryLoad(paths);
        if (journal is null)
        {
            InfoMessage = SkipNotRecordedMessage;
            return false;
        }

        if (PhaseReconciliation.Reconcile(paths, journal))
        {
            _stateStore.ReplacePhases(paths, [.. journal.Phases]);
        }

        ApplyJournal(journal);
        var next = PhaseReconciliation.FirstIncomplete(journal);
        ResumePhase = next;

        if (StatusOf(WorkflowPhase.Specification) != PhaseStatus.Completed)
        {
            // Continue workflow now resumes at that phase; nothing was skipped.
            var resume = PhaseCatalog.For(next ?? WorkflowPhase.Specification).DisplayName;
            InfoMessage = $"Implementierung nicht gestartet: Spezifikation oder Plan fehlt oder ist leer. Nächste Phase: {resume}.";
            return false;
        }

        // Step 5 (D19): one write, verified by reading it back. A review that did complete keeps its
        // green - it happened - and the indicators show what the journal holds after the write.
        var skipped = _stateStore.TrySkipPhases(paths, SkippablePhases);
        if (skipped is null)
        {
            InfoMessage = SkipNotRecordedMessage;
            return false;
        }

        ApplyJournal(skipped);
        return true;
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
