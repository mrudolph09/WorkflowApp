using Workflow.Models;

namespace Workflow.Tests;

// The scheduler is pure: a snapshot and the titles attempted this run in, exactly one plan out.
public class SubtaskSchedulerTests
{
    private static readonly IReadOnlySet<string> NothingAttempted =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> Attempted(params string[] titles) =>
        new HashSet<string>(titles, StringComparer.OrdinalIgnoreCase);

    private static SubtaskState Entry(
        string title,
        SubtaskStatus status,
        string? reason = null,
        params string[]? dependsOn) =>
        new(title, status, reason, dependsOn);

    private static SubtaskState Defaulted(string title, SubtaskStatus status, string? reason = null) =>
        new(title, status, reason);

    private static SubtaskPlan Plan(IReadOnlySet<string> attempted, params SubtaskState[] states) =>
        SubtaskScheduler.Plan(new SubtaskSnapshot(states), attempted);

    private static string RunnableTitle(SubtaskPlan plan)
    {
        Assert.Equal(SubtaskPlanKind.Runnable, plan.Kind);
        Assert.NotNull(plan.Next);
        return plan.Next.Title;
    }

    private static IReadOnlyList<string> BlockedReasons(SubtaskPlan plan)
    {
        Assert.Equal(SubtaskPlanKind.TerminallyBlocked, plan.Kind);
        Assert.Null(plan.Next);
        return plan.Reasons;
    }

    [Fact]
    public void Plan_RunsTheFirstUnfinishedEntryInIndexOrder()
    {
        var plan = Plan(
            NothingAttempted,
            Defaulted("ST-001-alpha", SubtaskStatus.Complete),
            Defaulted("ST-002-beta", SubtaskStatus.Pending),
            Defaulted("ST-003-gamma", SubtaskStatus.Pending));

        Assert.Equal("ST-002-beta", RunnableTitle(plan));
    }

    // Default dependencies are "every previous entry": a waiting entry holds back everything after
    // it, so no later session is started just to report "blocked".
    [Fact]
    public void Plan_HoldsBackEveryLaterEntryBehindAWaitingOneByDefault()
    {
        var plan = Plan(
            Attempted("ST-001-alpha"),
            Defaulted("ST-001-alpha", SubtaskStatus.WaitingForHuman, "Freigabe fehlt"),
            Defaulted("ST-002-beta", SubtaskStatus.Pending),
            Defaulted("ST-003-gamma", SubtaskStatus.Pending));

        var blocked = BlockedReasons(plan);
        Assert.Contains(
            "ST-002-beta wartet auf ST-001-alpha (waiting_for_human: Freigabe fehlt)",
            blocked);
    }

    // An explicit empty dependency list lets an independent entry run past a waiting one.
    [Fact]
    public void Plan_RunsAnIndependentEntryPastAWaitingOne()
    {
        var plan = Plan(
            Attempted("ST-001-alpha"),
            Defaulted("ST-001-alpha", SubtaskStatus.WaitingForHuman, "Freigabe fehlt"),
            Defaulted("ST-002-beta", SubtaskStatus.Pending),
            Entry("ST-003-gamma", SubtaskStatus.Pending, null));

        Assert.Equal("ST-003-gamma", RunnableTitle(plan));
    }

    // A waiting or blocked entry not yet attempted this run is runnable itself: a new run is the
    // moment to see whether whatever it waited for has arrived.
    [Theory]
    [InlineData(SubtaskStatus.Pending)]
    [InlineData(SubtaskStatus.Failed)]
    [InlineData(SubtaskStatus.Blocked)]
    [InlineData(SubtaskStatus.WaitingForHuman)]
    public void Plan_RunsAnUnattemptedEntryInEveryRetriableStatus(SubtaskStatus status)
    {
        var plan = Plan(
            NothingAttempted,
            Defaulted("ST-001-alpha", SubtaskStatus.Complete),
            Defaulted("ST-002-beta", status, "Grund"));

        Assert.Equal("ST-002-beta", RunnableTitle(plan));
    }

    [Fact]
    public void Plan_NeverRunsAnEntryAttemptedThisRun()
    {
        var plan = Plan(
            Attempted("st-002-BETA"),
            Defaulted("ST-001-alpha", SubtaskStatus.Complete),
            Defaulted("ST-002-beta", SubtaskStatus.Failed, "rot"));

        var blocked = BlockedReasons(plan);
        Assert.Equal(["ST-002-beta (failed: rot)"], blocked);
    }

    // A failed dependency with only dependents left: nothing can run, and the reason names the chain.
    [Fact]
    public void Plan_ReportsTheChainWhenOnlyDependentsOfAFailedEntryRemain()
    {
        var plan = Plan(
            Attempted("ST-001-alpha"),
            Defaulted("ST-001-alpha", SubtaskStatus.Failed, "Build rot"),
            Entry("ST-002-beta", SubtaskStatus.Pending, null, "ST-001-alpha"),
            Entry("ST-003-gamma", SubtaskStatus.Pending, null, "ST-002-beta"));

        var blocked = BlockedReasons(plan);
        Assert.Equal(
            [
                "ST-002-beta wartet auf ST-001-alpha (failed: Build rot)",
                "ST-003-gamma wartet auf ST-002-beta (pending)",
            ],
            blocked);
    }

    // Independent work first, then the failed chain: the scheduler offers the independent entry
    // and, once that is attempted, ends with the chain.
    [Fact]
    public void Plan_RunsIndependentWorkBeforeReportingTheFailedChain()
    {
        SubtaskState[] states =
        [
            Defaulted("ST-001-alpha", SubtaskStatus.Failed, "Build rot"),
            Entry("ST-002-beta", SubtaskStatus.Pending, null, "ST-001-alpha"),
            Entry("ST-003-gamma", SubtaskStatus.Pending, null),
        ];

        var first = Plan(Attempted("ST-001-alpha"), states);
        Assert.Equal("ST-003-gamma", RunnableTitle(first));

        var second = Plan(Attempted("ST-001-alpha", "ST-003-gamma"), states);
        var blocked = BlockedReasons(second);
        Assert.Contains("ST-002-beta wartet auf ST-001-alpha (failed: Build rot)", blocked);
    }

    // A dependency that completes during the run releases its dependents in the same run: entries
    // skipped for unmet dependencies are never marked attempted.
    [Fact]
    public void Plan_ReleasesADependentOnceItsDependencyCompletes()
    {
        var plan = Plan(
            Attempted("ST-001-alpha"),
            Defaulted("ST-001-alpha", SubtaskStatus.Complete),
            Defaulted("ST-002-beta", SubtaskStatus.Pending));

        Assert.Equal("ST-002-beta", RunnableTitle(plan));
    }

    [Fact]
    public void Plan_NeverRunsADeferredEntryButWaitsForItsDesktopWindow()
    {
        var plan = Plan(
            Attempted("ST-002-beta"),
            Defaulted("ST-001-alpha", SubtaskStatus.Complete),
            Defaulted("ST-002-beta", SubtaskStatus.Deferred, "Desktop-Fenster"),
            Defaulted("ST-003-gamma", SubtaskStatus.Pending));

        Assert.Equal(SubtaskPlanKind.WaitingForDesktopWindow, plan.Kind);
        Assert.Equal(["ST-002-beta"], plan.Titles);
    }

    // Runnable work outranks waiting for a desktop job.
    [Fact]
    public void Plan_PrefersRunnableWorkOverWaitingForADesktopJob()
    {
        var plan = Plan(
            NothingAttempted,
            Defaulted("ST-001-alpha", SubtaskStatus.Deferred),
            Entry("ST-002-beta", SubtaskStatus.Pending, null));

        Assert.Equal("ST-002-beta", RunnableTitle(plan));
    }

    // A deferred entry whose own dependencies are not complete is not waiting for a desktop job; it
    // is blocked like any other dependent.
    [Fact]
    public void Plan_DoesNotWaitForADeferredEntryWhoseDependenciesAreIncomplete()
    {
        var plan = Plan(
            Attempted("ST-001-alpha"),
            Defaulted("ST-001-alpha", SubtaskStatus.Failed, "rot"),
            Defaulted("ST-002-beta", SubtaskStatus.Deferred));

        Assert.Equal(SubtaskPlanKind.TerminallyBlocked, plan.Kind);
    }

    // A dependency the snapshot cannot resolve to an earlier entry is never satisfied, even when
    // the snapshot did not come from the ledger.
    [Fact]
    public void Plan_NeverRunsAnEntryWithADependencyOnALaterOrUnknownEntry()
    {
        var plan = Plan(
            NothingAttempted,
            Entry("ST-001-alpha", SubtaskStatus.Pending, null, "ST-002-beta"),
            Entry("ST-002-beta", SubtaskStatus.Complete, null),
            Entry("ST-003-gamma", SubtaskStatus.Pending, null, "ST-404"));

        var blocked = BlockedReasons(plan);
        Assert.Equal(2, blocked.Count);
    }

    [Fact]
    public void Plan_IsDoneWhenEveryEntryIsComplete()
    {
        var plan = Plan(
            Attempted("ST-001-alpha"),
            Defaulted("ST-001-alpha", SubtaskStatus.Complete),
            Entry("ST-002-beta", SubtaskStatus.Complete, null));

        Assert.Equal(SubtaskPlanKind.Done, plan.Kind);
    }

    [Fact]
    public void Plan_IsDoneForAnEmptySnapshot() =>
        Assert.Equal(SubtaskPlanKind.Done, Plan(NothingAttempted).Kind);

    [Fact]
    public void Plan_RejectsMissingArguments()
    {
        Assert.Throws<ArgumentNullException>(() => SubtaskScheduler.Plan(null!, NothingAttempted));
        Assert.Throws<ArgumentNullException>(() => SubtaskScheduler.Plan(new SubtaskSnapshot([]), null!));
    }

    // --- Desktop jobs: a delivered result makes the deferred entry runnable (JobResultReady) ---

    private static SubtaskState Ready(string title) =>
        new(title, SubtaskStatus.Deferred, null, null, JobResultReady: true);

    [Fact]
    public void Plan_RunsADeferredEntryWhoseJobResultIsReady()
    {
        var plan = Plan(
            NothingAttempted,
            Ready("ST-001-alpha"),
            Defaulted("ST-002-beta", SubtaskStatus.Pending));

        Assert.Equal("ST-001-alpha", RunnableTitle(plan));
    }

    [Fact]
    public void Plan_DoesNotWaitForAReadyEntryThatWasAlreadyAttempted()
    {
        var plan = Plan(Attempted("ST-001-alpha"), Ready("ST-001-alpha"));

        Assert.Equal(SubtaskPlanKind.TerminallyBlocked, plan.Kind);
    }

    [Fact]
    public void Plan_KeepsWaitingForADeferredEntryWithoutAReadyResult()
    {
        var plan = Plan(Attempted("ST-001-alpha"), Defaulted("ST-001-alpha", SubtaskStatus.Deferred));

        Assert.Equal(SubtaskPlanKind.WaitingForDesktopWindow, plan.Kind);
    }

    private static SubtaskState Broken(string title) =>
        new(title, SubtaskStatus.Deferred, "Desktop-Job wartet auf Leerlauffenster", null, JobNeedsRepair: true);

    [Fact]
    public void Plan_RunsADeferredEntryWhoseJobNeedsRepair()
    {
        var plan = Plan(NothingAttempted, Broken("ST-001-alpha"), Defaulted("ST-002-beta", SubtaskStatus.Pending));

        Assert.Equal("ST-001-alpha", RunnableTitle(plan));
    }

    [Fact]
    public void DesktopJobsWaiting_NeverListsAnEntryWhoseJobNeedsRepair()
    {
        var snapshot = new SubtaskSnapshot([Broken("ST-001-alpha") with { DependsOn = [] }, Defaulted("ST-002-beta", SubtaskStatus.Deferred) with { DependsOn = [] }]);

        Assert.Equal(["ST-002-beta"], SubtaskScheduler.DesktopJobsWaiting(snapshot));
    }

    [Fact]
    public void Plan_DescribesAnAttemptedEntryWhoseJobStillNeedsRepair()
    {
        var reasons = BlockedReasons(Plan(Attempted("ST-001-alpha"), Broken("ST-001-alpha")));

        Assert.Equal(["ST-001-alpha (deferred: Desktop-Job fehlgeschlagen, keine neue Anforderung)"], reasons);
    }

    [Fact]
    public void Plan_EndsTerminallyBlockedWithTheReasonOfAnExhaustedDesktopJob()
    {
        var exhausted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ST-001-alpha"] = "Desktop-Job ST-001-alpha/serie erschöpft: timeout",
        };

        var plan = SubtaskScheduler.Plan(
            new SubtaskSnapshot(
            [
                Defaulted("ST-001-alpha", SubtaskStatus.Deferred),
                Defaulted("ST-002-beta", SubtaskStatus.Pending),
            ]),
            NothingAttempted,
            exhausted);

        var reasons = BlockedReasons(plan);
        Assert.Equal("Desktop-Job ST-001-alpha/serie erschöpft: timeout", reasons[0]);
        Assert.Contains(reasons, reason => reason.StartsWith("ST-002-beta wartet auf ST-001-alpha", StringComparison.Ordinal));
    }

    [Fact]
    public void DesktopJobsWaiting_ListsWaitingEntriesEvenWhenAnotherEntryIsRunnable()
    {
        var snapshot = new SubtaskSnapshot(
        [
            Defaulted("ST-001-alpha", SubtaskStatus.Deferred),
            Entry("ST-002-beta", SubtaskStatus.Pending, null),
            Entry("ST-003-gamma", SubtaskStatus.Deferred, null),
            Ready("ST-004-delta") with { DependsOn = [] },
        ]);

        Assert.Equal("ST-002-beta", RunnableTitle(SubtaskScheduler.Plan(snapshot, NothingAttempted)));
        Assert.Equal(["ST-001-alpha", "ST-003-gamma"], SubtaskScheduler.DesktopJobsWaiting(snapshot));
        Assert.Equal(
            ["ST-003-gamma"],
            SubtaskScheduler.DesktopJobsWaiting(
                snapshot,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["ST-001-alpha"] = "x" }));
    }
}
