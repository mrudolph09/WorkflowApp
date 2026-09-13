using System.Windows;
using System.Windows.Controls;
using Workflow.ViewModels;

namespace Workflow.Views;

/// <summary>Hosts the xterm.js terminal and the single-line input box.</summary>
public partial class TerminalView : UserControl
{
    /// <summary>Creates the view.</summary>
    public TerminalView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Idempotent: a TabControl hosts the selected item in ONE content host, so re-selecting
        // this tab raises Loaded again on a view model that is already attached and running.
        if (DataContext is TerminalViewModel viewModel)
        {
            await viewModel.AttachAsync(TerminalWebView);
        }
    }
}
