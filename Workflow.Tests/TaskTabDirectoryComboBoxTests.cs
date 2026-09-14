using System.IO;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using Workflow.Models;
using Workflow.Services;
using Workflow.Terminal;
using Workflow.ViewModels;

namespace Workflow.Tests;

public sealed class TaskTabDirectoryComboBoxTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wf-combo-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsService _settings;

    public TaskTabDirectoryComboBoxTests()
    {
        Directory.CreateDirectory(_root);
        _settings = new SettingsService(Path.Combine(_root, "settings.json"));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed class Orc : IWorkflowOrchestrator
    {
        public Task RunAsync(WorkflowRunRequest r, CancellationToken c) => Task.Delay(Timeout.Infinite, c);
    }

    private sealed class Picker : IDirectoryPickerService
    {
        public string? PickDirectory(string? initialDirectory) => null;
    }

#pragma warning disable CA2000
    // Regression: OnWorkingDirectoryChanged used to rebuild RecentDirectories with Clear() +
    // Add(). Because the ComboBox binds SelectedItem TwoWay, the empty ItemsSource made the
    // Selector write null back into WorkingDirectory, so picking a second directory blanked the
    // box and left 'Start workflow' disabled.
    [WpfFact]
    public void SelectingASecondDirectory_KeepsTheSelection()
    {
        var a = Path.Combine(_root, "aaa"); Directory.CreateDirectory(a);
        var b = Path.Combine(_root, "bbb"); Directory.CreateDirectory(b);

        using var vm = new TaskTabViewModel(
            new TaskFolderService(), new Orc(), _settings, new Picker(),
            new TerminalViewModel(new WebViewEnvironmentProvider(), new ConPtySessionFactory(), Dispatcher.CurrentDispatcher),
            TimeSpan.FromMilliseconds(50), []);

        // Exactly what TaskTabView.xaml does.
        var combo = new ComboBox { DataContext = vm };
        combo.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.RecentDirectories)));
        combo.SetBinding(Selector.SelectedItemProperty,
            new Binding(nameof(vm.WorkingDirectory)) { Mode = BindingMode.TwoWay });

        vm.WorkingDirectory = a;
        Assert.Equal(a, vm.WorkingDirectory);

        vm.WorkingDirectory = b;   // e.g. the Browse button picking a different folder
        Assert.Equal(b, vm.WorkingDirectory);
        Assert.Equal(b, combo.SelectedItem);
        Assert.Equal(new[] { b, a }, vm.RecentDirectories);
    }

    // A picker result with a trailing separator must still match the normalised MRU entry,
    // otherwise the Selector finds no matching item and nulls the selection again.
    [WpfFact]
    public void ATrailingSeparator_DoesNotBreakTheSelection()
    {
        var a = Path.Combine(_root, "aaa"); Directory.CreateDirectory(a);

        using var vm = new TaskTabViewModel(
            new TaskFolderService(), new Orc(), _settings, new Picker(),
            new TerminalViewModel(new WebViewEnvironmentProvider(), new ConPtySessionFactory(), Dispatcher.CurrentDispatcher),
            TimeSpan.FromMilliseconds(50), []);

        var combo = new ComboBox { DataContext = vm };
        combo.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.RecentDirectories)));
        combo.SetBinding(Selector.SelectedItemProperty,
            new Binding(nameof(vm.WorkingDirectory)) { Mode = BindingMode.TwoWay });

        vm.WorkingDirectory = a + Path.DirectorySeparatorChar;

        Assert.Equal(a, vm.WorkingDirectory);
        Assert.Equal(a, combo.SelectedItem);
    }
#pragma warning restore CA2000
}
