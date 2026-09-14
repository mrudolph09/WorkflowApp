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
    private readonly ITaskTabViewModelFactory _factory;
    private readonly ITaskRecoveryScanner _scanner;

    [ObservableProperty]
    private TaskTabViewModel? _selectedTab;

    /// <summary>Creates the shell view model and opens the initial tab.</summary>
    /// <param name="factory">Creates tab view models.</param>
    /// <param name="scanner">Finds interrupted tasks to offer as prefilled tabs.</param>
    /// <param name="startupErrors">Prompt-template validation errors, if any.</param>
    public MainWindowViewModel(
        ITaskTabViewModelFactory factory,
        ITaskRecoveryScanner scanner,
        IReadOnlyList<string> startupErrors)
    {
        _factory = factory;
        _scanner = scanner;
        StartupErrors = startupErrors;

        AddTaskTab();
    }

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
            var tab = _factory.Create();
            tab.CloseRequested += OnTabCloseRequested;
            tab.LoadForResume(task);
            Tabs.Add(tab);
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

    [RelayCommand]
    private void AddTaskTab()
    {
        var tab = _factory.Create();
        tab.CloseRequested += OnTabCloseRequested;
        Tabs.Add(tab);
        SelectedTab = tab;
    }

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
