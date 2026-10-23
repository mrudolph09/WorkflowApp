using System.IO;
using System.Reflection;
using Microsoft.Win32;
using Workflow.Services;

namespace Workflow.Tests;

/// <remarks>
/// The dialog is modal and cannot be shown in a test host, so these tests inspect the dialog object
/// the service prepares - the same split <see cref="DirectoryPickerServiceTests"/> relies on.
/// </remarks>
public sealed class FilePickerServiceTests : IDisposable
{
    private readonly string _root;

    public FilePickerServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wf-filepicker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static OpenFileDialog Prepare(string? initialDirectory, string title)
    {
        var method = typeof(FilePickerService).GetMethod("CreateDialog", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "FilePickerService has no private static CreateDialog; the prepared dialog is unobservable again.");

        return (OpenFileDialog)method.Invoke(null, [initialDirectory, title])!;
    }

    // [StaFact]: the dialog is a WPF component and its constructor wants an STA thread.
    [StaFact]
    public void PickMarkdownFile_PreparingItsDialog_AsksForOneExistingMarkdownFileInTheGivenFolder()
    {
        // Act
        var dialog = Prepare(_root, "Spezifikation auswählen (wird zu alpha_spec.md)");

        // Assert
        Assert.Equal("Spezifikation auswählen (wird zu alpha_spec.md)", dialog.Title);
        Assert.Equal(_root, dialog.InitialDirectory);
        Assert.Equal("Markdown-Dateien (*.md)|*.md|Alle Dateien (*.*)|*.*", dialog.Filter);
        Assert.True(dialog.CheckFileExists);
        Assert.False(dialog.Multiselect);
    }

    [StaFact]
    public void PickMarkdownFile_StartFolderMissing_LeavesTheDialogsDefaultFolder()
    {
        // Act
        var dialog = Prepare(Path.Combine(_root, "gibt-es-nicht"), "Plan auswählen (wird zu alpha_plan.md)");

        // Assert
        Assert.True(string.IsNullOrEmpty(dialog.InitialDirectory));
    }
}
