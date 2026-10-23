namespace Workflow.Services;

/// <summary>The time source and the waiting of desktop jobs, substitutable so tests need not wait.</summary>
public interface IDesktopJobClock
{
    /// <summary>The current time in UTC.</summary>
    public DateTimeOffset UtcNow { get; }

    /// <summary>Waits for <paramref name="interval"/>.</summary>
    /// <param name="interval">How long to wait.</param>
    /// <param name="cancellationToken">Ends the wait with an <see cref="OperationCanceledException"/>.</param>
    /// <returns>A task that completes after the interval.</returns>
    public Task DelayAsync(TimeSpan interval, CancellationToken cancellationToken);
}

/// <summary>The real clock: <see cref="DateTimeOffset.UtcNow"/> and <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</summary>
public sealed class SystemDesktopJobClock : IDesktopJobClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public Task DelayAsync(TimeSpan interval, CancellationToken cancellationToken) =>
        Task.Delay(interval, cancellationToken);
}
