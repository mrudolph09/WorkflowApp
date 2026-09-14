using System.Windows.Threading;
using Workflow.Terminal;
using Workflow.ViewModels;

namespace Workflow.Services;

/// <inheritdoc cref="ITaskTabViewModelFactory" />
public sealed class TaskTabViewModelFactory : ITaskTabViewModelFactory
{
    private static readonly TimeSpan FolderDebounce = TimeSpan.FromMilliseconds(500);

    private readonly ITaskFolderService _folders;
    private readonly IWorkflowOrchestrator _orchestrator;
    private readonly ISettingsService _settings;
    private readonly ITaskStateStore _stateStore;
    private readonly IDirectoryPickerService _picker;
    private readonly IWebViewEnvironmentProvider _environment;
    private readonly ITerminalSessionFactory _sessions;
    private readonly Dispatcher _dispatcher;
    private readonly IReadOnlyList<string> _startupErrors;

    /// <summary>Creates the factory.</summary>
    /// <param name="folders">Task-folder service.</param>
    /// <param name="orchestrator">The four-phase state machine.</param>
    /// <param name="settings">Shared settings.</param>
    /// <param name="stateStore">The per-task workflow journal, shared by every tab.</param>
    /// <param name="picker">Folder-browser dialog.</param>
    /// <param name="environment">Shared WebView2 environment.</param>
    /// <param name="sessions">Pseudo-console session factory.</param>
    /// <param name="dispatcher">UI dispatcher.</param>
    /// <param name="startupErrors">
    /// Prompt-template problems found by <c>PromptTemplateService.ValidateAll()</c>. The factory
    /// carries them so that EVERY tab it makes - including tabs opened with '+' long after the
    /// startup dialog was dismissed - has Start disabled (F17, spec section 9.4).
    /// </param>
    public TaskTabViewModelFactory(
        ITaskFolderService folders,
        IWorkflowOrchestrator orchestrator,
        ISettingsService settings,
        ITaskStateStore stateStore,
        IDirectoryPickerService picker,
        IWebViewEnvironmentProvider environment,
        ITerminalSessionFactory sessions,
        Dispatcher dispatcher,
        IReadOnlyList<string> startupErrors)
    {
        ArgumentNullException.ThrowIfNull(startupErrors);

        _folders = folders;
        _orchestrator = orchestrator;
        _settings = settings;
        _stateStore = stateStore;
        _picker = picker;
        _environment = environment;
        _sessions = sessions;
        _dispatcher = dispatcher;
        _startupErrors = startupErrors;
    }

    /// <inheritdoc />
    public TaskTabViewModel Create() => new(
        _folders,
        _orchestrator,
        _settings,
        _stateStore,
        _picker,
        new TerminalViewModel(_environment, _sessions, _dispatcher),
        FolderDebounce,
        _startupErrors);
}
