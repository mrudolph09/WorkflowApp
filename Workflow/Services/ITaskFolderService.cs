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

    /// <summary>
    /// Lists the canonical specification and plan paths that are missing or empty (0 bytes) in the
    /// task folder - but only when the folder holds at least one other Markdown file that could be
    /// adopted as one of them (Workflow_LoadFix spec section 6.2). A canonical file whose size
    /// cannot be read counts as present, so it is never offered for replacement.
    /// </summary>
    /// <param name="paths">The task's path set.</param>
    /// <returns>
    /// <see cref="TaskPaths.SpecAbsolute"/> and/or <see cref="TaskPaths.PlanAbsolute"/>, in that order;
    /// empty when both hold content, when the folder holds no candidate, or when it cannot be read.
    /// </returns>
    public IReadOnlyList<string> FindMissingPlanningArtefacts(TaskPaths paths);

    /// <summary>
    /// Renames (moves) a user-chosen file to a missing canonical artefact path. A 0-byte file at
    /// <paramref name="artefactPath"/> is replaced; any other existing file is never overwritten.
    /// </summary>
    /// <param name="sourceFile">The file the user chose; it may lie outside the task folder.</param>
    /// <param name="artefactPath">The canonical path it becomes.</param>
    /// <exception cref="ArgumentException">An argument is null, empty or white space.</exception>
    /// <exception cref="System.IO.IOException">The target holds content, or the move failed.</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be moved.</exception>
    public void AdoptArtefact(string sourceFile, string artefactPath);
}
