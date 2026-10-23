using Workflow.Services;

namespace Workflow.Tests;

/// <remarks>Workflow_LOAD_AND_PAUSE spec section 6.2.</remarks>
public sealed class PhasePauseGateTests
{
    private static readonly TimeSpan Released = TimeSpan.FromSeconds(5);

    [Fact]
    public void ANewGate_IsOpenAndDoesNotHold()
    {
        // Arrange
        var gate = new PhasePauseGate();

        // Act
        var wait = gate.WaitWhilePausedAsync(CancellationToken.None);

        // Assert
        Assert.False(gate.IsPaused);
        Assert.True(wait.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Pause_HoldsTheWaitUntilUnpause()
    {
        // Arrange
        var gate = new PhasePauseGate();
        gate.Pause();
        var wait = gate.WaitWhilePausedAsync(CancellationToken.None);

        // Act
        var heldBeforeUnpause = !wait.IsCompleted;
        gate.Unpause();

        // Assert
        Assert.True(heldBeforeUnpause);
        await wait.WaitAsync(Released);
        Assert.False(gate.IsPaused);
    }

    [Fact]
    public async Task PausingTwice_IsReleasedByOneUnpause()
    {
        // Arrange
        var gate = new PhasePauseGate();
        gate.Pause();
        var first = gate.WaitWhilePausedAsync(CancellationToken.None);
        gate.Pause();
        var second = gate.WaitWhilePausedAsync(CancellationToken.None);

        // Act
        gate.Unpause();

        // Assert
        await Task.WhenAll(first, second).WaitAsync(Released);
        Assert.True(gate.WaitWhilePausedAsync(CancellationToken.None).IsCompletedSuccessfully);
    }

    [Fact]
    public void Unpause_OnAnOpenGate_IsANoOp()
    {
        // Arrange
        var gate = new PhasePauseGate();

        // Act
        gate.Unpause();

        // Assert
        Assert.False(gate.IsPaused);
        Assert.True(gate.WaitWhilePausedAsync(CancellationToken.None).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task PauseAfterUnpause_HoldsAgain()
    {
        // Arrange
        var gate = new PhasePauseGate();
        gate.Pause();
        gate.Unpause();

        // Act
        gate.Pause();
        var wait = gate.WaitWhilePausedAsync(CancellationToken.None);

        // Assert
        Assert.False(wait.IsCompleted);
        gate.Unpause();
        await wait.WaitAsync(Released);
    }

    [Fact]
    public async Task APausedWait_EndsWhenItsTokenIsCancelledAndLeavesTheGatePaused()
    {
        // Arrange
        var gate = new PhasePauseGate();
        gate.Pause();
        using var cts = new CancellationTokenSource();
        var wait = gate.WaitWhilePausedAsync(cts.Token);

        // Act
        await cts.CancelAsync();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.True(gate.IsPaused);
    }

    [Fact]
    public void Paused_IsRaisedOnlyWhenTheGateActuallyCloses()
    {
        // Arrange
        var gate = new PhasePauseGate();
        var raised = 0;
        gate.Paused += (_, _) => raised++;

        // Act
        gate.Pause();
        gate.Pause();
        gate.Unpause();
        gate.Pause();

        // Assert - two closings; the repeated Pause and the Unpause raise nothing.
        Assert.Equal(2, raised);
        Assert.True(gate.IsPaused);
    }
}
