using Workflow.Models;

namespace Workflow.Services;

/// <summary>Reads and writes one task's workflow journal. No method ever throws.</summary>
public interface ITaskStateStore
{
    /// <summary>Reads the journal.</summary>
    /// <param name="paths">The task's path set.</param>
    /// <returns>
    /// The journal, normalised to every catalogue phase in order; null when the file is absent,
    /// unreadable, not valid JSON, or written by a newer schema version.
    /// </returns>
    public TaskState? TryLoad(TaskPaths paths);

    /// <summary>
    /// Writes a fresh journal - every phase <see cref="PhaseStatus.Pending"/> and an empty
    /// description - when the task folder exists and holds no journal file at all.
    /// </summary>
    /// <param name="paths">The task's path set.</param>
    /// <returns>
    /// True when this call wrote the journal. False when a journal file already exists - readable or
    /// not, it is never overwritten, not even one another creator publishes while this call runs -
    /// when the task folder does not exist, or when the write failed.
    /// </returns>
    /// <remarks>
    /// Only <c>Task laden</c> calls this (Workflow_LoadFix spec section 5). Unlike
    /// <see cref="SaveDescription"/> it never creates the task folder and never replaces an
    /// unreadable journal: such a file may be a newer build's or a hand edit in progress. It is a
    /// create-only publication of its own and does not go through the store's overwriting save.
    /// </remarks>
    public bool TryCreate(TaskPaths paths);

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
    /// Records each listed phase as <see cref="PhaseStatus.Skipped"/> - stamped like a completion -
    /// unless the journal already records it Completed or Skipped, all in ONE read-modify-write,
    /// then reads the journal back.
    /// </summary>
    /// <param name="paths">The task's path set.</param>
    /// <param name="phases">The phases to skip; all of them share one timestamp.</param>
    /// <returns>
    /// The read-back journal when it exists and every listed phase reads Completed or Skipped;
    /// null when there is no readable journal or the write did not land.
    /// </returns>
    /// <remarks>
    /// Never creates a journal. Never throws. Unlike <see cref="RecordPhase"/> the outcome is
    /// proven: the store's save swallows a failed write, so only the read-back shows that the skip
    /// is durable (Workflow_LoadFix spec 8.3, D19). Each phase is judged by the status read inside
    /// this read-modify-write, so a phase another writer recorded Completed stays Completed.
    /// </remarks>
    public TaskState? TrySkipPhases(TaskPaths paths, IReadOnlyCollection<WorkflowPhase> phases);

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

    /// <summary>
    /// Records the subtask mode and its tracking directory, creating the journal if needed.
    /// </summary>
    /// <param name="paths">The task's path set.</param>
    /// <param name="enabled">True when the task runs its implementation phase in subtask mode.</param>
    /// <param name="workflowDirectory">The tracking directory, or null while the mode is off.</param>
    /// <remarks>
    /// Unlike <see cref="SaveDescription"/> this deliberately leaves the dismissed flag alone.
    /// Persisting a configuration is not the user starting or continuing the task, so it must not
    /// pull a dismissed task back into the recovery offer. Callers are the task start, an edit
    /// against an existing task folder, and the configuration captured at subtask phase entry.
    /// </remarks>
    public void SaveSubtaskSettings(TaskPaths paths, bool enabled, string? workflowDirectory);

    /// <summary>
    /// Sets or clears the manual-override marker for the implementation phase. A no-op when there
    /// is no journal.
    /// </summary>
    /// <param name="paths">The task's path set.</param>
    /// <param name="completedManually">True when the user ended implementation explicitly.</param>
    /// <remarks>
    /// Recovery honours the marker without consulting the subtask ledger, so a task the user
    /// stopped deliberately is never reopened by the evidence on disk. Like
    /// <see cref="SetDismissed"/> this corrects an existing journal and never invents one.
    /// </remarks>
    public void SetImplementationCompletedManually(TaskPaths paths, bool completedManually);
}
