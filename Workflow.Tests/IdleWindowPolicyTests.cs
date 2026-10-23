using Workflow.Models;

namespace Workflow.Tests;

public sealed class IdleWindowPolicyTests
{
    [Theory]
    [InlineData(15, 15, false, false, true)]
    [InlineData(16, 15, false, false, true)]
    [InlineData(14, 15, false, false, false)]
    [InlineData(60, 15, true, false, false)]
    [InlineData(60, 15, false, true, false)]
    [InlineData(0, 1, false, false, false)]
    public void IsOpen_RequiresEnoughIdleTimeAnUnlockedSessionAndNoScreenSaver(
        int idleMinutes,
        int requiredMinutes,
        bool locked,
        bool screenSaver,
        bool expected)
    {
        var activity = new UserActivity(TimeSpan.FromMinutes(idleMinutes), locked, screenSaver);

        Assert.Equal(expected, IdleWindowPolicy.IsOpen(activity, requiredMinutes));
    }

    [Fact]
    public void IsOpen_OneSecondShortOfTheLimitIsStillClosed()
    {
        var activity = new UserActivity(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1), false, false);

        Assert.False(IdleWindowPolicy.IsOpen(activity, 15));
    }
}
