using System.IO;

namespace Workflow.Models;

/// <summary>
/// Result of validating the Workflows tracking directory that subtask mode runs against, together
/// with the validation itself.
/// </summary>
/// <param name="IsValid">True when the directory may be used as a tracking repository.</param>
/// <param name="ErrorMessage">German message to show the user, or null when valid.</param>
/// <remarks>
/// <para>
/// Requirement 1.3 asks for one explanatory German message per invalid case, and requirement 1.8
/// re-runs the same check at phase-4 entry, so the flag and the message travel together: the tab
/// gates the start action on <see cref="IsValid"/> and presents <see cref="ErrorMessage"/> when the
/// configuration is rejected. The shape mirrors <see cref="TaskNameValidation"/>.
/// </para>
/// <para>
/// A tracking repository is recognised by the <see cref="SubtaskPaths.TemplateFolderName"/> folder
/// alone. A <c>.git</c> folder is deliberately <em>not</em> required (requirement 1.3): users copy
/// or export the tracking repository without git metadata, and the application performs no git
/// operations against it.
/// </para>
/// </remarks>
public sealed record WorkflowDirectoryValidation(bool IsValid, string? ErrorMessage)
{
    /// <summary>The successful result.</summary>
    public static WorkflowDirectoryValidation Ok { get; } = new(true, null);

    /// <summary>Creates a failed result.</summary>
    /// <param name="message">German message to show the user.</param>
    /// <returns>A failed validation result.</returns>
    public static WorkflowDirectoryValidation Error(string message) => new(false, message);

    /// <summary>Validates a selected tracking directory.</summary>
    /// <param name="workflowDirectory">
    /// The selection, exactly as it arrives from the recent-directory selector or the folder picker;
    /// null, empty and whitespace are expected inputs, not programming errors.
    /// </param>
    /// <returns>
    /// <see cref="Ok"/>, or a failed result whose message names the reason: nothing selected, the
    /// directory does not exist, or it is not a tracking repository.
    /// </returns>
    /// <remarks>
    /// The selection is normalised through <see cref="WorkingDirectoryPath.Normalise"/> before it is
    /// tested, exactly as <see cref="SubtaskPaths"/> normalises the same root, so a trailing
    /// separator from the picker cannot make an otherwise identical directory validate differently
    /// or be reported back to the user in a second spelling.
    /// </remarks>
    public static WorkflowDirectoryValidation Validate(string? workflowDirectory)
    {
        if (string.IsNullOrWhiteSpace(workflowDirectory))
        {
            return Error("Bitte ein Workflow-Verzeichnis auswählen.");
        }

        var directory = WorkingDirectoryPath.Normalise(workflowDirectory);

        if (!Directory.Exists(directory))
        {
            return Error($"Das Workflow-Verzeichnis '{directory}' existiert nicht.");
        }

        // A file of the same name is not the marker: the template is a folder the tracking
        // contract reads further paths from, so Directory.Exists is the deliberate check.
        if (!Directory.Exists(Path.Combine(directory, SubtaskPaths.TemplateFolderName)))
        {
            return Error(
                $"Das Verzeichnis '{directory}' enthält keinen Ordner "
                + $"'{SubtaskPaths.TemplateFolderName}' und ist daher nicht das ausgecheckte Workflows-Repository.");
        }

        return Ok;
    }
}
