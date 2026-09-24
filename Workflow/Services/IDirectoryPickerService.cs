namespace Workflow.Services;

/// <summary>Shows the folder-browser dialog.</summary>
public interface IDirectoryPickerService
{
    /// <summary>Caption used when a caller does not supply one of its own. Deliberate addition beyond the members design names: a default parameter value must be a compile-time constant, so this keeps the caption literal in one place instead of repeating it across the interface, the service and the test doubles.</summary>
    public const string DefaultTitle = "Arbeitsverzeichnis auswählen";

    /// <summary>Asks the user for a directory.</summary>
    /// <param name="initialDirectory">Directory to start in, or null.</param>
    /// <param name="title">Caption for the dialog; the working-directory caption by default.</param>
    /// <returns>The chosen directory, or null when cancelled.</returns>
    public string? PickDirectory(string? initialDirectory, string title = DefaultTitle);
}
