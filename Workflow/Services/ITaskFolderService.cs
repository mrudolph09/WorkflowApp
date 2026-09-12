using Workflow.Models;

namespace Workflow.Services;

/// <summary>Creates, renames and validates the folder that holds a task's artefacts.</summary>
public interface ITaskFolderService
{
    /// <summary>Checks whether a task name can be used as a folder name.</summary>
    /// <param name="taskName">The proposed task name.</param>
    /// <param name="workingDirectory">The selected working directory.</param>
    /// <returns>A validation result carrying a German message on failure.</returns>
    public TaskNameValidation Validate(string? taskName, string? workingDirectory);

    /// <summary>Creates the task folder if it does not exist. Existing content is preserved.</summary>
    /// <param name="paths">The task's path set.</param>
    public void EnsureCreated(TaskPaths paths);

    /// <summary>Reports whether the task folder is already present on disk.</summary>
    /// <param name="paths">The task's path set.</param>
    /// <returns>True when the folder exists.</returns>
    public bool DirectoryAlreadyExisted(TaskPaths paths);

    /// <summary>Renames the task folder. A no-op when the source folder is absent.</summary>
    /// <param name="workingDirectory">Parent directory of both names.</param>
    /// <param name="oldName">Current folder name.</param>
    /// <param name="newName">Desired folder name.</param>
    public void Rename(string workingDirectory, string oldName, string newName);
}
