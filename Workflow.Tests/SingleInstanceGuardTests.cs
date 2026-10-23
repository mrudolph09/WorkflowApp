using Workflow.Services;

namespace Workflow.Tests;

public sealed class SingleInstanceGuardTests
{
    private static string UniqueName() => @"Local\Workflow.Tests." + Guid.NewGuid().ToString("N");

    [Fact]
    public void TryAcquire_GrantsTheFirstHolder()
    {
        using var guard = SingleInstanceGuard.TryAcquire(UniqueName());

        Assert.NotNull(guard);
    }

    [Fact]
    public void TryAcquire_RefusesASecondHolderOfTheSameName()
    {
        var name = UniqueName();
        using var first = SingleInstanceGuard.TryAcquire(name);

        using var second = SingleInstanceGuard.TryAcquire(name);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public void TryAcquire_GrantsTheNameAgainAfterTheHolderIsDisposed()
    {
        var name = UniqueName();
        SingleInstanceGuard.TryAcquire(name)!.Dispose();

        using var again = SingleInstanceGuard.TryAcquire(name);

        Assert.NotNull(again);
    }
}
