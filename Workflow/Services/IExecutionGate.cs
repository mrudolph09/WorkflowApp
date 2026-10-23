namespace Workflow.Services;

/// <summary>
/// The application-wide gate between agent sessions and desktop jobs. Every agent session of every
/// tab holds it shared; a desktop job holds it exclusively, so its measurements run on a machine no
/// session disturbs.
/// </summary>
/// <remarks>
/// Writer preference: once an exclusive request waits, no new shared hold is granted. The sessions
/// already running finish, the job runs, and the sessions that queued meanwhile start afterwards.
/// </remarks>
public interface IExecutionGate
{
    /// <summary>Waits for a shared hold, as an agent session needs one.</summary>
    /// <param name="cancellationToken">Ends a pending wait with an <see cref="OperationCanceledException"/>.</param>
    /// <returns>The hold; disposing it releases it. Disposing twice releases once.</returns>
    public Task<IDisposable> AcquireSharedAsync(CancellationToken cancellationToken);

    /// <summary>Waits for the exclusive hold, as a desktop job needs it.</summary>
    /// <param name="cancellationToken">Ends a pending wait with an <see cref="OperationCanceledException"/>.</param>
    /// <returns>The hold; disposing it releases it. Disposing twice releases once.</returns>
    public Task<IDisposable> AcquireExclusiveAsync(CancellationToken cancellationToken);
}
