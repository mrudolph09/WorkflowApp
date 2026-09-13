namespace Workflow.Services;

/// <summary>Shows the folder-browser dialog.</summary>
public interface IDirectoryPickerService
{
    /// <summary>Asks the user for a directory.</summary>
    /// <param name="initialDirectory">Directory to start in, or null.</param>
    /// <returns>The chosen directory, or null when cancelled.</returns>
    public string? PickDirectory(string? initialDirectory);
}
