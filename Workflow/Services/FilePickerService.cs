using System.IO;
using Microsoft.Win32;

namespace Workflow.Services;

/// <inheritdoc cref="IFilePickerService" />
public sealed class FilePickerService : IFilePickerService
{
    /// <inheritdoc />
    public string? PickMarkdownFile(string? initialDirectory, string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var dialog = CreateDialog(initialDirectory, title);

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <summary>
    /// Configures the dialog without showing it, so the configuration is observable in a test:
    /// <c>ShowDialog</c> is modal and cannot run in a test host.
    /// </summary>
    private static OpenFileDialog CreateDialog(string? initialDirectory, string title)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "Markdown-Dateien (*.md)|*.md|Alle Dateien (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog;
    }
}
