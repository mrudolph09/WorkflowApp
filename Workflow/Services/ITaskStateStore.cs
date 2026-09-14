using Workflow.Models;

namespace Workflow.Services;

/// <summary>Reads and writes one task's workflow journal. No method ever throws.</summary>
public interface ITaskStateStore
{
    /// <summary>Reads the journal.</summary>
    /// <param name="paths">The task's path set.</param>
    /// <returns>
    /// The journal, normalised to four phases in catalogue order; null when the file is absent,
    /// unreadable, not valid JSON, or written by a newer schema version.
    /// </returns>
    public TaskState? TryLoad(TaskPaths paths);

    /// <summary>
    /// Records the Taskbeschreibung, creating the journal if needed, and clears the dismissed
    /// flag - the user has explicitly started or continued this task.
    /// </summary>
    /// <param name="paths">The task's path set.</param>
    /// <param name="taskDescription">The description to persist.</param>
    public void SaveDescription(TaskPaths paths, string taskDescription);

    /// <summary>Records one phase's status, creating the journal if needed.</summary>
    /// <param name="paths">The task's path set.</param>
    /// <param name="phase">The phase whose status changed.</param>
    /// <param name="status">The new status.</param>
    public void RecordPhase(TaskPaths paths, WorkflowPhase phase, PhaseStatus status);

    /// <summary>
    /// Overwrites the whole phase array with <paramref name="phases"/>, normalised. A no-op when
    /// there is no journal, and the one mutator that leaves <c>UpdatedUtc</c> untouched.
    /// </summary>
    /// <param name="paths">The task's path set.</param>
    /// <param name="phases">The reconciled phases; order and length are normalised.</param>
    /// <remarks>
    /// Exists so that the demote-never-promote reconciliation of SPEC section 6.3 is durable.
    /// <c>RecordPhase</c> touches one entry, so a resumed run would otherwise leave the demoted
    /// LATER phases marked Completed on disk and the next crash would skip them. The timestamp is
    /// deliberately not stamped: a reconciliation records what the disk already said, and bumping
    /// it would keep a task inside the 14-day recovery window purely because a file was deleted.
    /// </remarks>
    public void ReplacePhases(TaskPaths paths, IReadOnlyList<TaskPhaseState> phases);

    /// <summary>Sets or clears the dismissed flag. A no-op when there is no journal.</summary>
    /// <param name="paths">The task's path set.</param>
    /// <param name="dismissed">The new value.</param>
    public void SetDismissed(TaskPaths paths, bool dismissed);
}
