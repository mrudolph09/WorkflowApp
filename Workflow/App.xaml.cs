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

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Workflow");

        var settings = new SettingsService(SettingsService.DefaultPath);
        var prompts = new PromptTemplateService(Path.Combine(AppContext.BaseDirectory, "Prompt"));
        var autoAnswer = new AutoAnswerService(
            Path.Combine(AppContext.BaseDirectory, "Assets", "autoanswer.rules.json"),
            Path.Combine(appData, "autoanswer.rules.json"));

        var stateStore = new TaskStateStore();

        var orchestrator = new WorkflowOrchestrator(
            prompts,
            autoAnswer,
            new ArtifactWatcherFactory(),
            stateStore,
            TimeSpan.FromMilliseconds(750),
            TimeSpan.FromSeconds(1));

        // Turns a silent runtime FileNotFoundException into an actionable startup message.
        // Validated BEFORE the factory is built: the factory hands the result to every tab it
        // makes, so Start stays disabled even on tabs opened after the dialog is dismissed.
        var startupErrors = prompts.ValidateAll();

        // CA2000: WebViewEnvironmentProvider is a deliberate app-wide singleton (spec 5.3/6.4 -
        // "one CoreWebView2Environment for the whole application") that lives for the process
        // lifetime; nothing in this composition root disposes any of its long-lived services.
#pragma warning disable CA2000
        var factory = new TaskTabViewModelFactory(
            new TaskFolderService(),
            orchestrator,
            settings,
            new DirectoryPickerService(),
            new WebViewEnvironmentProvider(),
            new ConPtySessionFactory(),
            Dispatcher,
            startupErrors);
#pragma warning restore CA2000

        _shell = new MainWindowViewModel(factory, startupErrors);

        var window = new MainWindow { DataContext = _shell };
        window.Closing += (_, _) => _shell.ShutdownAll();
        MainWindow = window;
        window.Show();

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
