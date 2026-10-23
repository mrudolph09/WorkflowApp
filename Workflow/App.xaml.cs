using System.IO;
using System.Windows;
using System.Windows.Threading;
using Workflow.Services;
using Workflow.Terminal;
using Workflow.ViewModels;
using Workflow.Views;

namespace Workflow;

/// <summary>Application entry point and composition root.</summary>
public partial class App : Application
{
    private MainWindowViewModel? _shell;
    private SingleInstanceGuard? _singleInstance;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // One instance per Windows session: the execution gate below is app-wide only if the app
        // is. A second start hands over to the first and ends before building anything.
        _singleInstance = SingleInstanceGuard.TryAcquire(SingleInstanceGuard.ApplicationMutexName);
        if (_singleInstance is null)
        {
            SingleInstanceGuard.ActivateOtherInstance();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Workflow");

        var settings = new SettingsService(SettingsService.DefaultPath);
        var prompts = new PromptTemplateService(Path.Combine(AppContext.BaseDirectory, "Prompt"));
        var autoAnswer = new AutoAnswerService(
            Path.Combine(AppContext.BaseDirectory, "Assets", "autoanswer.rules.json"),
            Path.Combine(appData, "autoanswer.rules.json"));

        var stateStore = new TaskStateStore();

        // One gate and one desktop job runner for the whole application: every tab shares the
        // orchestrator, so every agent session holds this gate shared and every desktop job holds
        // it exclusively. The window yield resolves MainWindow when a job runs, not now.
        var desktopJobs = new DesktopJobRunner(
            new Win32UserActivityMonitor(),
            new SystemCpuLoadProbe(),
            new PwshDesktopJobProcessStarter(),
            new SystemDesktopJobClock(),
            new WindowForegroundYield(Dispatcher, () => MainWindow));

        var orchestrator = new WorkflowOrchestrator(
            prompts,
            autoAnswer,
            new ArtifactWatcherFactory(),
            stateStore,
            TimeSpan.FromMilliseconds(750),
            TimeSpan.FromSeconds(1),
            gate: new ExecutionGate(),
            desktopJobs: desktopJobs);

        // Turns a silent runtime FileNotFoundException into an actionable startup message.
        // Validated BEFORE the factory is built: the factory hands the result to every tab it
        // makes, so Start stays disabled even on tabs opened after the dialog is dismissed.
        var startupErrors = prompts.ValidateAll();

        // One picker for the whole application: the tabs use it for their directories and the shell
        // for 'Task laden'. It holds no state.
        var picker = new DirectoryPickerService();

        // One folder service as well: the tabs create and rename their task folders with it, and the
        // shell adopts a misnamed spec and plan on 'Task laden' (Workflow_LoadFix spec 6). Stateless.
        var folders = new TaskFolderService();

        // One message box service: the shell reports errors with it, the tabs ask their Ja/Nein
        // questions with it. Stateless.
        var messages = new MessageDialogService();

        // CA2000: WebViewEnvironmentProvider is a deliberate app-wide singleton (spec 5.3/6.4 -
        // "one CoreWebView2Environment for the whole application") that lives for the process
        // lifetime; nothing in this composition root disposes any of its long-lived services.
#pragma warning disable CA2000
        var factory = new TaskTabViewModelFactory(
            folders,
            orchestrator,
            settings,
            stateStore,
            picker,
            new WebViewEnvironmentProvider(),
            new ConPtySessionFactory(),
            Dispatcher,
            startupErrors,
            messages);
#pragma warning restore CA2000

        var scanner = new TaskRecoveryScanner(
            settings,
            stateStore,
            maxAge: TimeSpan.FromDays(14),
            maxTasks: 5,
            maxSubdirectoriesPerRoot: 2000,
            scanTimeout: TimeSpan.FromSeconds(5));

        _shell = new MainWindowViewModel(
            factory,
            scanner,
            picker,
            new FilePickerService(),
            folders,
            messages,
            settings,
            startupErrors);

        var window = new MainWindow { DataContext = _shell };
        window.Closing += (_, _) => _shell.ShutdownAll();
        MainWindow = window;
        window.Show();

        // Not awaited: the window must appear immediately even when an MRU entry is a
        // disconnected network share. The recovered tabs materialise a moment later.
        _ = _shell.InitialiseAsync();

        // Not awaited either, and deliberately silent: the installer downloads from the network,
        // which is far too slow to hold up the window and far too unreliable to let it block a
        // start. A run begun in the next few seconds simply uses the CLI already on the machine.
        _ = new CodexUpdateService().UpdateAsync();

        if (startupErrors.Count > 0)
        {
            MessageBox.Show(
                window,
                "Die Prompt-Vorlagen sind fehlerhaft:\n\n" + string.Join("\n", startupErrors),
                "Workflow",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        _shell?.ShutdownAll();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Top-level resilience boundary: a background failure must not kill a live workflow run.
        MessageBox.Show(
            MainWindow,
            "Ein unerwarteter Fehler ist aufgetreten:\n\n" + e.Exception.Message,
            "Workflow",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }
}
