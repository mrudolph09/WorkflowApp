using Workflow.Services;

namespace Workflow.Tests;

/// <remarks>
/// The app-wide reader/writer gate: agent sessions share it, desktop jobs hold it exclusively, and a
/// waiting job stops new sessions from starting (writer preference).
/// </remarks>
public sealed class ExecutionGateTests
{
    private static readonly TimeSpan Released = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task SharedAcquisitions_RunConcurrently()
    {
        var gate = new ExecutionGate();

        using var first = await gate.AcquireSharedAsync(CancellationToken.None).WaitAsync(Released);
        using var second = await gate.AcquireSharedAsync(CancellationToken.None).WaitAsync(Released);

        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task Exclusive_WaitsForEveryRunningSharedHolder()
    {
        var gate = new ExecutionGate();
        var first = await gate.AcquireSharedAsync(CancellationToken.None);
        var second = await gate.AcquireSharedAsync(CancellationToken.None);

        var exclusive = gate.AcquireExclusiveAsync(CancellationToken.None);
        Assert.False(exclusive.IsCompleted);

        first.Dispose();
        Assert.False(exclusive.IsCompleted);

        second.Dispose();
        using var held = await exclusive.WaitAsync(Released);
    }

    [Fact]
    public async Task WaitingExclusive_BlocksNewSharedAcquisitions()
    {
        var gate = new ExecutionGate();
        var running = await gate.AcquireSharedAsync(CancellationToken.None);

        var exclusive = gate.AcquireExclusiveAsync(CancellationToken.None);
        var lateShared = gate.AcquireSharedAsync(CancellationToken.None);

        // Writer preference: the job is queued, so the new session must not overtake it.
        Assert.False(lateShared.IsCompleted);

        running.Dispose();
        var job = await exclusive.WaitAsync(Released);
        Assert.False(lateShared.IsCompleted);

        job.Dispose();
        using var resumed = await lateShared.WaitAsync(Released);
    }

    [Fact]
    public async Task SharedWaiters_AllResumeAfterTheExclusiveRelease()
    {
        var gate = new ExecutionGate();
        var job = await gate.AcquireExclusiveAsync(CancellationToken.None);

        var a = gate.AcquireSharedAsync(CancellationToken.None);
        var b = gate.AcquireSharedAsync(CancellationToken.None);
        Assert.False(a.IsCompleted);
        Assert.False(b.IsCompleted);

        job.Dispose();

        using var first = await a.WaitAsync(Released);
        using var second = await b.WaitAsync(Released);
    }

    [Fact]
    public async Task ACancelledExclusiveWaiter_LetsQueuedSharedWaitersThrough()
    {
        var gate = new ExecutionGate();
        var running = await gate.AcquireSharedAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();

        var exclusive = gate.AcquireExclusiveAsync(cts.Token);
        var lateShared = gate.AcquireSharedAsync(CancellationToken.None);
        Assert.False(lateShared.IsCompleted);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exclusive.WaitAsync(Released));
        using var shared = await lateShared.WaitAsync(Released);
        running.Dispose();
    }

    [Fact]
    public async Task ACancelledSharedWaiter_ThrowsAndDoesNotLeakAHold()
    {
        var gate = new ExecutionGate();
        var job = await gate.AcquireExclusiveAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();

        var shared = gate.AcquireSharedAsync(cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => shared.WaitAsync(Released));

        job.Dispose();

        // Had the cancelled waiter been granted, this exclusive request would wait forever.
        using var next = await gate.AcquireExclusiveAsync(CancellationToken.None).WaitAsync(Released);
    }

    [Fact]
    public async Task DisposingAHandleTwice_ReleasesOnlyOnce()
    {
        var gate = new ExecutionGate();
        var first = await gate.AcquireSharedAsync(CancellationToken.None);
        var second = await gate.AcquireSharedAsync(CancellationToken.None);

        first.Dispose();
        first.Dispose();

        var exclusive = gate.AcquireExclusiveAsync(CancellationToken.None);
        Assert.False(exclusive.IsCompleted);

        second.Dispose();
        using var held = await exclusive.WaitAsync(Released);
    }

    [Fact]
    public async Task TwoTabs_AQueuedSessionCannotStartWhileAJobHoldsTheGate()
    {
        // Two consumers of the one app-wide gate, as two tabs sharing the orchestrator are.
        var gate = new ExecutionGate();
        var events = new List<string>();
        var jobStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishJob = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tabA = Task.Run(async () =>
        {
            using (await gate.AcquireExclusiveAsync(CancellationToken.None))
            {
                lock (events)
                {
                    events.Add("job-start");
                }

                jobStarted.SetResult();
                await finishJob.Task;

                lock (events)
                {
                    events.Add("job-end");
                }
            }
        });

        await jobStarted.Task.WaitAsync(Released);

        var tabB = Task.Run(async () =>
        {
            using (await gate.AcquireSharedAsync(CancellationToken.None))
            {
                lock (events)
                {
                    events.Add("session");
                }
            }
        });

        await Task.Delay(100);
        lock (events)
        {
            Assert.DoesNotContain("session", events);
        }

        finishJob.SetResult();
        await Task.WhenAll(tabA, tabB).WaitAsync(Released);

        Assert.Equal(["job-start", "job-end", "session"], events);
    }
}
