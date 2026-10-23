namespace Workflow.Services;

/// <summary>
/// The tab's "Desktop-Job jetzt starten" button as seen by the run: while the run idles in a
/// desktop-job wait, one press ends that one wait early. The run brackets each wait with
/// <see cref="BeginWait"/> and <see cref="EndWait"/>; the tab shows the button only between them.
/// </summary>
/// <remarks>
/// A press outside a wait is dropped, never stored (WF_DESKTOPJOBBUTTON spec A1): a press made
/// while nothing waits must not skip a later wait. That is why this is not
/// <see cref="ManualPhaseSignal"/> (spec D4), which keeps a press until it is reset. Built like
/// <see cref="PhasePauseGate"/>: a lock around a <see cref="TaskCompletionSource"/> created with
/// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>, so <see cref="Request"/> -
/// called on the UI thread by the button - never runs the orchestrator's continuation inline.
/// </remarks>
public sealed class DesktopJobStartSignal
{
    private readonly object _gate = new();

    // Null while nothing waits. During a wait: the source that wait is holding.
    private TaskCompletionSource? _waiting;

    /// <summary>True between <see cref="BeginWait"/> and the matching <see cref="EndWait"/>.</summary>
    public bool IsWaiting
    {
        get
        {
            lock (_gate)
            {
                return _waiting is not null;
            }
        }
    }

    /// <summary>
    /// Raised after <see cref="BeginWait"/> and after <see cref="EndWait"/>, on the caller's thread
    /// and outside the lock.
    /// </summary>
    public event EventHandler? WaitingChanged;

    /// <summary>
    /// Starts a new wait, replacing any current one, so a press made earlier is never carried in.
    /// </summary>
    /// <returns>A task that completes on <see cref="Request"/>; it never faults or cancels.</returns>
    public Task BeginWait()
    {
        Task wait;

        lock (_gate)
        {
            _waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            wait = _waiting.Task;
        }

        WaitingChanged?.Invoke(this, EventArgs.Empty);

        return wait;
    }

    /// <summary>Ends the current wait.</summary>
    public void EndWait()
    {
        lock (_gate)
        {
            _waiting = null;
        }

        WaitingChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Completes the current wait. A no-op while nothing waits; a second press on the same wait
    /// changes nothing.
    /// </summary>
    public void Request()
    {
        lock (_gate)
        {
            _waiting?.TrySetResult();
        }
    }
}
