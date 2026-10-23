using Workflow.Models;

namespace Workflow.Services;

/// <summary>Finds tasks that were interrupted, so they can be offered as prefilled tabs.</summary>
public interface ITaskRecoveryScanner
{
    /// <summary>Scans the directory MRU for unfinished tasks.</summary>
    /// <param name="cancellationToken">Cancels the scan; partial results are still returned.</param>
    /// <returns>
    /// At most <c>maxTasks</c> tasks, most recently updated first. Never throws: a scan that
    /// fails must not stop the application from starting.
    /// </returns>
    public Task<IReadOnlyList<RecoverableTask>> ScanAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads the one task folder the user picked with <c>Task laden</c>, creating its journal first
    /// when the folder holds none (Workflow_LoadFix spec section 5).
    /// </summary>
    /// <param name="paths">The task's path set, derived from the chosen folder.</param>
    /// <returns>
    /// The task, reconciled against its artefacts; its <see cref="RecoverableTask.ResumePhase"/> is
    /// null when every phase is done, and <see cref="RecoverableTask.JournalCreated"/> is true when
    /// this call wrote the journal. Null when the folder holds a journal that cannot be read, or
    /// holds none and none could be written.
    /// </returns>
    /// <remarks>
    /// Unlike <see cref="ScanAsync"/> this applies neither the age window, nor the dismissed filter,
    /// nor the directory MRU: the user chose this folder explicitly. Only this method ever creates a
    /// journal; the scan never does. Every write (the new journal, a reconciliation change, clearing
    /// the dismissed flag) is best-effort: the store never throws.
    /// </remarks>
    public RecoverableTask? Load(TaskPaths paths);

    /// <summary>
    /// True when the task folder exists and holds no journal file or a readable one.
    /// </summary>
    /// <param name="paths">The task's path set, derived from the chosen folder.</param>
    /// <returns>
    /// False when the folder is gone or its journal cannot be read (invalid JSON, newer schema
    /// version): <see cref="Load"/> would then return null.
    /// </returns>
    /// <remarks>
    /// Writes nothing: no journal, no reconciliation, no dismissed flag. It is the read-only
    /// preflight of <see cref="Load"/> for the takeover flow, so that no user file is ever renamed
    /// for a load that cannot succeed (Workflow_LoadFix spec 6.2, D21).
    /// </remarks>
    public bool CanLoad(TaskPaths paths);
}
