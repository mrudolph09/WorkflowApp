using System.IO;
using Microsoft.Win32;

namespace Workflow.Services;

/// <inheritdoc cref="IDirectoryPickerService" />
public sealed class DirectoryPickerService : IDirectoryPickerService
{
    /// <summary>
    /// How this service builds its dialog. Always <see cref="CreateDialog" /> in production; it is a
    /// field only so a test can observe what <see cref="PickDirectory" /> forwards, which is otherwise
    /// unreachable because the step after it is a modal window.
    /// </summary>
    private Func<string?, string, OpenFolderDialog> _createDialog = CreateDialog;

    /// <inheritdoc />
    public string? PickDirectory(string? initialDirectory, string title = IDirectoryPickerService.DefaultTitle)
    {
        var dialog = _createDialog(initialDirectory, title);

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    /// <summary>
    /// Configures the dialog without showing it. Split out of <see cref="PickDirectory" /> only so
    /// that the caption a caller asked for is observable: <c>ShowDialog</c> is modal and cannot run
    /// in a test host, but the configured dialog object can be inspected. Behaviour is unchanged -
    /// the caller still shows this dialog and reads the same result from it.
    /// </summary>
    private static OpenFolderDialog CreateDialog(string? initialDirectory, string title)
    {
        // OpenFolderDialog is the .NET 8 WPF folder browser; no Windows Forms reference needed.
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog;
    }
}
