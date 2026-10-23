using Workflow.Services;

namespace Workflow.Tests;

/// <remarks>WF_DESKTOPJOBBUTTON spec section 4.2.</remarks>
public sealed class DesktopJobStartSignalTests
{
    private static readonly TimeSpan Released = TimeSpan.FromSeconds(5);

    [Fact]
    public void Request_BeforeAWait_DoesNotCompleteTheNextWait()
    {
        // Arrange
        var signal = new DesktopJobStartSignal();
        signal.Request();

        // Act
        var wait = signal.BeginWait();

        // Assert
        Assert.False(wait.IsCompleted);
    }

    [Fact]
    public async Task Request_DuringAWait_CompletesIt()
    {
        // Arrange
        var signal = new DesktopJobStartSignal();
        var wait = signal.BeginWait();

        // Act
        signal.Request();
        signal.Request();

        // Assert
        await wait.WaitAsync(Released);
        Assert.True(wait.IsCompletedSuccessfully);
    }

    [Fact]
    public void BeginAndEndWait_SetIsWaitingAndRaiseWaitingChangedEachTime()
    {
        // Arrange
        var signal = new DesktopJobStartSignal();
        var seen = new List<bool>();
        signal.WaitingChanged += (_, _) => seen.Add(signal.IsWaiting);

        // Act
        signal.BeginWait();
        signal.EndWait();

        // Assert
        Assert.Equal([true, false], seen);
        Assert.False(signal.IsWaiting);
    }
}
