using System.IO;
using Microsoft.Win32;

namespace Workflow.Services;

/// <inheritdoc cref="IDirectoryPickerService" />
public sealed class DirectoryPickerService : IDirectoryPickerService
{
    /// <inheritdoc />
    public string? PickDirectory(string? initialDirectory)
    {
        // OpenFolderDialog is the .NET 8 WPF folder browser; no Windows Forms reference needed.
        var dialog = new OpenFolderDialog
        {
            Title = "Arbeitsverzeichnis auswählen",
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
