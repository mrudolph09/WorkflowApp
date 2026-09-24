using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Input;
using System.Windows.Threading;
using System.Xml.Linq;
using Workflow.Models;
using Workflow.Services;
using Workflow.Terminal;
using Workflow.Tests.Fakes;
using Workflow.ViewModels;

namespace Workflow.Tests;

/// <remarks>
/// <para>
/// Task 6.5 removes the manual <c>Phase abschliessen</c> action: requirement 5.1 deletes it from
/// every phase indicator, and requirement 5.2 then leaves phases 1 to 3 with their artefact
/// condition as the only way forward, the terminal staying interactive if no artefact arrives.
/// </para>
/// <para>
/// These are absence tests, and an absence test is worthless unless it can see the thing it
/// denies. Every assertion here therefore carries a positive control asserted in the same method -
/// the surviving <c>Task abschliessen</c> action, the surviving converter binding, the surviving
/// indicator members - so a broken lookup path fails the test instead of passing it vacuously.
/// </para>
/// </remarks>
public sealed class PhaseCompletionRemovalTests : IDisposable
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private readonly string _root;
    private readonly string _workspace;
    private readonly string _settingsPath;
    private readonly SettingsService _settings;

    public PhaseCompletionRemovalTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wf-tab65-" + Guid.NewGuid().ToString("N"));
        _workspace = Path.Combine(_root, "arbeit");
        Directory.CreateDirectory(_workspace);

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

    // CA2000 cannot trace the disposal: TerminalViewModel's ownership passes into the returned
    // TaskTabViewModel, whose own Dispose() disposes it, and every call site uses `using var`.
#pragma warning disable CA2000
    private TaskTabViewModel CreateTab()
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

    private static string SourceRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Workflow"));

    private static string PhaseIndicatorMarkupPath() =>
        Path.Combine(SourceRoot(), "Views", "PhaseIndicatorView.xaml");

    // -----------------------------------------------------------------------------------------
    // Requirement 5.1: the indicator carries no completion action
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void ThePhaseIndicator_DeclaresNoActiveFlag()
    {
        var declared = typeof(PhaseIndicatorViewModel)
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToList();

        // Positive control: the members the indicator keeps. If this lookup ever returns an empty
        // or unrelated set, these three fail rather than letting the absence check pass for free.
        Assert.Contains("Phase", declared, StringComparer.Ordinal);
        Assert.Contains("DisplayName", declared, StringComparer.Ordinal);
        Assert.Contains("Status", declared, StringComparer.Ordinal);

        Assert.DoesNotContain("IsActive", declared, StringComparer.Ordinal);
    }

    [Fact]
    public void ChangingTheStatus_AnnouncesTheStatusAlone()
    {
        // The generated [NotifyPropertyChangedFor] relay is the half a deleted property cannot
        // prove on its own: with the attribute still in place the indicator would keep telling the
        // view about an activation flag that requirement 5.1 says no longer exists.
        var indicator = new PhaseIndicatorViewModel(PhaseCatalog.For(WorkflowPhase.Specification));

        var announced = new List<string>();
        indicator.PropertyChanged += (_, e) => announced.Add(e.PropertyName ?? string.Empty);

        indicator.Status = PhaseStatus.Active;

        Assert.Equal(PhaseStatus.Active, indicator.Status);
        Assert.Equal(new[] { nameof(PhaseIndicatorViewModel.Status) }, announced);
    }

    [Fact]
    public void ThePhaseIndicatorMarkup_CarriesNoCompletionButton()
    {
        var path = PhaseIndicatorMarkupPath();
        var markup = File.ReadAllText(path);
        var view = XDocument.Parse(markup);

        // Positive control: the markup really was loaded and really is the indicator.
        Assert.Contains("PhaseStatusToBrushConverter", markup, StringComparison.Ordinal);
        Assert.NotEmpty(view.Descendants(Presentation + "TextBlock"));

        Assert.Empty(view.Descendants(Presentation + "Button"));
        Assert.DoesNotContain("Phase abschliessen", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("CompleteCurrentPhase", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("IsActive", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTab_DeclaresNoPhaseCompletionAction()
    {
        var declared = typeof(TaskTabViewModel)
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToList();

        // Positive control: the task-completion action and the active-phase tracking it reads both
        // stay (requirement 5.3, design "Presentation and Recovery").
        Assert.Contains("CompleteTaskCommand", declared, StringComparer.Ordinal);
        Assert.Contains("_activePhase", declared, StringComparer.Ordinal);
        Assert.Contains("_manualSignal", declared, StringComparer.Ordinal);

        Assert.DoesNotContain(
            declared,
            name => name.Contains("CompleteCurrentPhase", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCompiledApplication_ShipsNoPhaseCompletionSurface()
    {
        // A .xaml file is not what the user runs. The German literal and the command name reach
        // the product as UTF-8 inside the compiled BAML, so the shipped assembly is where their
        // absence has to be proved (method carried forward from task 6.1).
        var assembly = File.ReadAllBytes(typeof(PhaseIndicatorViewModel).Assembly.Location);

        // Positive controls: strings of exactly the same kinds, from the same compiled markup and
        // the same metadata, that task 6.5 keeps. If the scan stopped working these fail first.
        Assert.True(Carries(assembly, "PhaseStatusToBrushConverter"), "The compiled BAML scan found no converter reference.");
        Assert.True(Carries(assembly, "Task abschliessen"), "The compiled BAML scan found no surviving German action label.");
        Assert.True(Carries(assembly, "CompleteTaskCommand"), "The compiled metadata scan found no surviving command name.");

        Assert.False(Carries(assembly, "Phase abschliessen"), "The shipped assembly still carries the 'Phase abschliessen' label.");
        Assert.False(Carries(assembly, "CompleteCurrentPhaseCommand"), "The shipped assembly still carries the phase-completion command.");
    }

    private static bool Carries(byte[] assembly, string literal) =>
        assembly.AsSpan().IndexOf(Encoding.UTF8.GetBytes(literal).AsSpan()) >= 0;

    // -----------------------------------------------------------------------------------------
    // Requirement 5.2: phases 1 to 3 advance on their artefact condition alone
    // -----------------------------------------------------------------------------------------

    [StaTheory]
    [InlineData(WorkflowPhase.Specification)]
    [InlineData(WorkflowPhase.Review)]
    [InlineData(WorkflowPhase.ResolveReview)]
    public void WhileAnEarlyPhaseRuns_TheTabOffersNoExecutableCompletionAction(WorkflowPhase phase)
    {
        // WorkflowOrchestrator.RunSessionAsync races the artefact watcher against the run's shared
        // ManualPhaseSignal for every phase. Nothing else in the application signals it, so
        // requirement 5.2 holds exactly as long as no executable command on a running tab can
        // reach Signal() while phases 1 to 3 are active.
        using var vm = CreateTab();
        vm.WorkingDirectory = _workspace;
        vm.TaskName = "demo";

        vm.StartWorkflowCommand.Execute(null);
        Assert.True(vm.IsRunning);

        vm.ApplyProgress(new PhaseProgress(phase, PhaseStatus.Active));

        Assert.Equal(
            new[] { "BrowseDirectoryCommand", "BrowseWorkflowDirectoryCommand", "CloseCommand" },
            ExecutableCommands(vm));

        // Positive control: the manual path is not dead, it is merely out of reach here. The very
        // same tab offers it once phase 4 is the active one (requirement 5.3).
        vm.ApplyProgress(new PhaseProgress(WorkflowPhase.Implementation, PhaseStatus.Active));
        Assert.Contains("CompleteTaskCommand", ExecutableCommands(vm), StringComparer.Ordinal);
    }

    private static List<string> ExecutableCommands(TaskTabViewModel vm) =>
        typeof(TaskTabViewModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => typeof(ICommand).IsAssignableFrom(p.PropertyType))
            .Where(p => ((ICommand)p.GetValue(vm)!).CanExecute(null))
            .Select(p => p.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
}
