using System.Windows;

namespace Workflow.Services;

/// <inheritdoc cref="IMessageDialogService" />
public sealed class MessageDialogService : IMessageDialogService
{
    /// <summary>The caption every message box of the application carries (App.xaml.cs uses it too).</summary>
    private const string Caption = "Workflow";

    /// <inheritdoc />
    public void ShowError(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        // Owned by the main window whenever there is one, so the box stays in front of it and is
        // modal to it.
        var owner = Application.Current?.MainWindow;

        if (owner is null)
        {
            MessageBox.Show(message, Caption, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        MessageBox.Show(owner, message, Caption, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
