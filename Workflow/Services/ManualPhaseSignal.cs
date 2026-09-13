namespace Workflow.Services;

/// <summary>
/// Lets the user end the current phase from the UI. This is the escape hatch that prevents a
/// permanent stall when a phase's artefact condition never becomes true.
/// </summary>
public sealed class ManualPhaseSignal
{
    private readonly object _gate = new();
    private TaskCompletionSource _source = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Ends the phase that is currently waiting.</summary>
    public void Signal()
    {
        lock (_gate)
        {
            _source.TrySetResult();
        }
    }

    /// <summary>Waits for the user to signal.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when Signal is called.</returns>
    public Task WaitAsync(CancellationToken cancellationToken)
    {
        Task task;
        lock (_gate)
        {
            task = _source.Task;
        }

        return task.WaitAsync(cancellationToken);
    }

    /// <summary>Rearms the signal for the next phase.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
