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

    /// <summary>Reads the one task folder the user picked with <c>Task laden</c>.</summary>
    /// <param name="paths">The task's path set, derived from the chosen folder.</param>
    /// <returns>
    /// The task, reconciled against its artefacts; its <see cref="RecoverableTask.ResumePhase"/> is
    /// null when every phase is completed. Null when the folder holds no readable journal.
    /// </returns>
    /// <remarks>
    /// Unlike <see cref="ScanAsync"/> this applies neither the age window, nor the dismissed filter,
    /// nor the directory MRU: the user chose this folder explicitly. Both write-backs (a demotion
    /// found by the reconciliation, and clearing the dismissed flag) are best-effort: the store
    /// never throws, so a journal that cannot be written still yields the reconciled task.
    /// </remarks>
    public RecoverableTask? Load(TaskPaths paths);
}
