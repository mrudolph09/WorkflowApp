namespace Workflow.Services;

/// <inheritdoc cref="IExecutionGate" />
/// <remarks>
/// <para>
/// An async reader/writer lock with writer preference, built like <see cref="PhasePauseGate"/>: one
/// lock around plain state and <see cref="TaskCompletionSource{T}"/> waiters created with
/// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>, so a release - which may happen
/// on the UI thread - never runs a waiter's continuation inline.
/// </para>
/// <para>
/// Waiters are granted only inside <see cref="Pump"/>, under the lock, and a cancelled waiter is
/// removed under the same lock; a waiter is therefore either granted or cancelled, never both, and a
/// cancelled request never leaks a hold.
/// </para>
/// </remarks>
public sealed class ExecutionGate : IExecutionGate
{
    private readonly object _gate = new();
    private readonly LinkedList<Waiter> _sharedWaiters = new();
    private readonly LinkedList<Waiter> _exclusiveWaiters = new();

    private int _sharedHolders;
    private bool _exclusiveHeld;

    /// <inheritdoc />
    public Task<IDisposable> AcquireSharedAsync(CancellationToken cancellationToken) =>
        Acquire(exclusive: false, cancellationToken);

    /// <inheritdoc />
    public Task<IDisposable> AcquireExclusiveAsync(CancellationToken cancellationToken) =>
        Acquire(exclusive: true, cancellationToken);

    private Task<IDisposable> Acquire(bool exclusive, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Waiter waiter;
        lock (_gate)
        {
            // The fast paths are exactly Pump's grant rules, applied to a request nobody queued yet.
            if (exclusive && !_exclusiveHeld && _sharedHolders == 0 && _exclusiveWaiters.Count == 0)
            {
                _exclusiveHeld = true;
                return Task.FromResult<IDisposable>(new Hold(this, exclusive: true));
            }

            if (!exclusive && !_exclusiveHeld && _exclusiveWaiters.Count == 0)
            {
                _sharedHolders++;
                return Task.FromResult<IDisposable>(new Hold(this, exclusive: false));
            }

            waiter = new Waiter(exclusive);
            waiter.Node = (exclusive ? _exclusiveWaiters : _sharedWaiters).AddLast(waiter);
        }

        if (cancellationToken.CanBeCanceled)
        {
            var registration = cancellationToken.Register(() => Cancel(waiter, cancellationToken));

            lock (_gate)
            {
                // Granted or cancelled in the meantime: nothing is left to cancel. Unregister, not
                // Dispose - Dispose waits for a running callback, which may be waiting for this lock.
                if (waiter.Node is null)
                {
                    registration.Unregister();
                }
                else
                {
                    waiter.Registration = registration;
                }
            }
        }

        return waiter.Source.Task;
    }

    private void Cancel(Waiter waiter, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (waiter.Node?.List is null)
            {
                // Already granted; the caller owns the hold and releases it as usual.
                return;
            }

            waiter.Node.List.Remove(waiter.Node);
            waiter.Node = null;
            waiter.Source.TrySetCanceled(cancellationToken);

            // A cancelled exclusive waiter may have been the only thing holding sessions back.
            Pump();
        }
    }

    private void Release(bool exclusive)
    {
        lock (_gate)
        {
            if (exclusive)
            {
                _exclusiveHeld = false;
            }
            else
            {
                _sharedHolders--;
            }

            Pump();
        }
    }

    /// <summary>Grants whatever may run now. Called under the lock.</summary>
    private void Pump()
    {
        if (_exclusiveHeld)
        {
            return;
        }

        if (_exclusiveWaiters.First is { } next)
        {
            // Writer preference: queued sessions keep waiting until every queued job has run.
            if (_sharedHolders == 0)
            {
                _exclusiveWaiters.RemoveFirst();
                _exclusiveHeld = true;
                Grant(next.Value);
            }

            return;
        }

        while (_sharedWaiters.First is { } shared)
        {
            _sharedWaiters.RemoveFirst();
            _sharedHolders++;
            Grant(shared.Value);
        }
    }

    private void Grant(Waiter waiter)
    {
        waiter.Node = null;

        // Unregister for the reason Acquire gives: this runs under the lock.
        waiter.Registration.Unregister();

        // CA2000: the hold is handed to the awaiting caller, who owns and disposes it.
#pragma warning disable CA2000
        waiter.Source.TrySetResult(new Hold(this, waiter.Exclusive));
#pragma warning restore CA2000
    }

    /// <summary>One queued request.</summary>
    private sealed class Waiter(bool exclusive)
    {
        public bool Exclusive { get; } = exclusive;

        public TaskCompletionSource<IDisposable> Source { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LinkedListNode<Waiter>? Node { get; set; }

        public CancellationTokenRegistration Registration { get; set; }
    }

    /// <summary>A granted hold. Releasing is idempotent.</summary>
    private sealed class Hold(ExecutionGate owner, bool exclusive) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(exclusive);
            }
        }
    }
}
