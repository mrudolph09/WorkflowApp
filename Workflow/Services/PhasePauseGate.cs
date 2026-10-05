namespace Workflow.Services;

/// <summary>
/// The user's pause switch for one tab's runs (Workflow_LOAD_AND_PAUSE spec section 6). While it is
/// paused, a run that reaches the boundary between two phases waits there; the running phase itself
/// is never touched.
/// </summary>
/// <remarks>
/// Built like <see cref="ManualPhaseSignal"/>: a lock around a <see cref="TaskCompletionSource"/>
/// created with <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>, so
/// <see cref="Unpause"/> - called on the UI thread by the Play button - never runs the
/// orchestrator's continuation inline inside the command handler.
/// </remarks>
public sealed class PhasePauseGate
{
    private readonly object _gate = new();

    // Null while open. While paused: the source every wait taken during this pause is holding.
    private TaskCompletionSource? _paused;

    /// <summary>True between <see cref="Pause"/> and the next <see cref="Unpause"/>.</summary>
    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _paused is not null;
            }
        }
    }

    /// <summary>Closes the gate. Pausing a paused gate keeps the waits already pending.</summary>
    public void Pause()
    {
        lock (_gate)
        {
            _paused ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Opens the gate and releases every pending wait. A no-op while it is open.</summary>
    public void Unpause()
    {
        lock (_gate)
        {
            _paused?.TrySetResult();
            _paused = null;
        }
    }

    /// <summary>Waits until the gate is open.</summary>
    /// <param name="cancellationToken">Ends a pending wait with an <see cref="OperationCanceledException"/>.</param>
    /// <returns>
    /// A completed task while the gate is open; otherwise a task that completes on the next
    /// <see cref="Unpause"/>.
    /// </returns>
    public Task WaitWhilePausedAsync(CancellationToken cancellationToken)
    {
        Task paused;

        lock (_gate)
        {
            paused = _paused?.Task ?? Task.CompletedTask;
        }

        return paused.WaitAsync(cancellationToken);
    }
}
