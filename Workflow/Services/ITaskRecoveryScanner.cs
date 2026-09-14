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
}
