namespace Workflow.Services;

/// <summary>Shows the open-file dialog.</summary>
/// <remarks>
/// A seam of the same shape as <see cref="IDirectoryPickerService"/>: the dialog is modal, so a view
/// model that showed one directly would block the test host.
/// </remarks>
public interface IFilePickerService
{
    /// <summary>Asks the user for one existing Markdown file.</summary>
    /// <param name="initialDirectory">Directory to start in, or null.</param>
    /// <param name="title">Caption of the dialog.</param>
    /// <returns>The chosen file's full path, or null when cancelled.</returns>
    public string? PickMarkdownFile(string? initialDirectory, string title);
}
