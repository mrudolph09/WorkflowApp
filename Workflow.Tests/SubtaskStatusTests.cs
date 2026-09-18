using System.Collections.ObjectModel;
using Workflow.Models;

namespace Workflow.Tests;

public class SubtaskStatusTests
{
    // Deliberately asymmetric, and every number here is load-bearing. Total 6, Completed 3, Failed 2
    // and Pending 1 are four DISTINCT values, so an implementation that counted Complete into Failed
    // (or either decisive value into the other, or Pending into either) reports a wrong number and is
    // caught. An earlier 2/2/1 fixture made Completed and Failed indistinguishable and let exactly
    // that swap pass. Order is pinned separately: the titles are all distinct, so a snapshot that
    // reversed the index order is caught by the title-based ordering and reason-pairing assertions.
    // Requirements 3.3 and 3.4 depend downstream on these two counters never being confused.
    private static readonly SubtaskState[] Known =
    [
        new("ST-001-parse", SubtaskStatus.Complete),
        new("ST-002-render", SubtaskStatus.Failed, "status.json meldete failed"),
        new("ST-003-persist", SubtaskStatus.Pending),
        new("ST-004-verify", SubtaskStatus.Failed, "subtask.md fehlt oder ist leer"),
        new("ST-005-ship", SubtaskStatus.Complete),
        new("ST-006-polish", SubtaskStatus.Complete),
    ];

    // Design, Data Models / Tracking Files (issue 1 resolved): the vocabulary is closed on the two
    // decisive values; everything else is Pending, so Pending must be the default value.
    [Fact]
    public void SubtaskStatus_IsClosedOnTheThreeStatesWithPendingAsTheDefault()
    {
        Assert.Equal(
            [SubtaskStatus.Pending, SubtaskStatus.Complete, SubtaskStatus.Failed],
            Enum.GetValues<SubtaskStatus>());
        Assert.Equal(SubtaskStatus.Pending, default(SubtaskStatus));
    }

    // Design, Presentation and Recovery: "stages are Idle, Decomposing, Running, Finished".
    [Fact]
    public void SubtaskStage_CarriesTheFourStagesWithIdleAsTheDefault()
    {
        Assert.Equal(
            [SubtaskStage.Idle, SubtaskStage.Decomposing, SubtaskStage.Running, SubtaskStage.Finished],
            Enum.GetValues<SubtaskStage>());
        Assert.Equal(SubtaskStage.Idle, default(SubtaskStage));
    }

    // Requirement 3.7: a subtask's failure reason has to survive as far as the tooltip, so the state
    // carries it; a state that has no reason carries null rather than an empty string.
    [Fact]
    public void SubtaskState_CarriesTitleStatusAndAnOptionalReason()
    {
        var failed = new SubtaskState("ST-002-render", SubtaskStatus.Failed, "status.json meldete failed");

        Assert.Equal("ST-002-render", failed.Title);
        Assert.Equal(SubtaskStatus.Failed, failed.Status);
        Assert.Equal("status.json meldete failed", failed.FailReason);

        var pending = new SubtaskState("ST-003-persist", SubtaskStatus.Pending);

        Assert.Null(pending.FailReason);
    }

    // Requirement 2.6 / design "Paths and Ledger": the counts the loop and the indicator agree on
    // are the counts of the states themselves - they are never supplied independently.
    [Fact]
    public void SubtaskSnapshot_DerivesEveryCountFromItsStates()
    {
        var snapshot = new SubtaskSnapshot(Known);

        Assert.Equal(6, snapshot.Total);
        Assert.Equal(3, snapshot.Completed);
        Assert.Equal(2, snapshot.Failed);

        // Total deliberately exceeds Completed + Failed: the one Pending entry must be counted by
        // neither decisive counter (design issue 1), which is only provable when the three numbers
        // differ from each other.
        Assert.Equal(1, snapshot.States.Count(state => state.Status == SubtaskStatus.Pending));
        Assert.Equal(snapshot.Total, snapshot.Completed + snapshot.Failed + 1);
    }

    // Design, Data Models: "SubtaskSnapshot contains ordered states". The index order is the
    // execution order (requirement 2.4), so the snapshot must not reorder or regroup it.
    [Fact]
    public void SubtaskSnapshot_KeepsTheStatesInIndexOrder()
    {
        var snapshot = new SubtaskSnapshot(Known);

        Assert.Equal(
            ["ST-001-parse", "ST-002-render", "ST-003-persist", "ST-004-verify", "ST-005-ship", "ST-006-polish"],
            snapshot.States.Select(state => state.Title));
    }

    // Design, Data Models (issue 1 resolved): a freshly decomposed index is all Pending and must
    // report zero failures, so requirement 3.3's "{K} fehlgeschlagen" never appears for it.
    [Fact]
    public void SubtaskSnapshot_ReportsNoFailuresForAFreshlyDecomposedIndex()
    {
        var snapshot = new SubtaskSnapshot(
        [
            new SubtaskState("ST-001-parse", SubtaskStatus.Pending),
            new SubtaskState("ST-002-render", SubtaskStatus.Pending),
        ]);

        Assert.Equal(2, snapshot.Total);
        Assert.Equal(0, snapshot.Completed);
        Assert.Equal(0, snapshot.Failed);
    }

    // Design, Presentation and Recovery (issue 6a): green requires Total > 0 && Completed == Total,
    // so an empty index must report zero rather than looking like a finished run.
    [Fact]
    public void SubtaskSnapshot_ReportsZeroForAnEmptyStateList()
    {
        var snapshot = new SubtaskSnapshot([]);

        Assert.Empty(snapshot.States);
        Assert.Equal(0, snapshot.Total);
        Assert.Equal(0, snapshot.Completed);
        Assert.Equal(0, snapshot.Failed);
    }

    [Fact]
    public void SubtaskSnapshot_RejectsAMissingStateList()
    {
        Assert.Throws<ArgumentNullException>(() => new SubtaskSnapshot(null!));
    }

    // Requirement 3.7 / design issue 7: the ordered states travel in the progress payload, which is
    // what delivers each failed entry's title and reason to the indicator tooltip.
    [Fact]
    public void SubtaskProgress_RoundTripsEveryFailureReason()
    {
        var snapshot = new SubtaskSnapshot(Known);

        var progress = new SubtaskProgress(
            SubtaskStage.Running,
            snapshot.Completed,
            snapshot.Total,
            snapshot.Failed,
            "ST-003-persist",
            snapshot.States);

        // Three distinct numbers, so each positional argument is pinned to its own property: a
        // Completed/Failed swap in the record declaration cannot survive this.
        Assert.Equal(SubtaskStage.Running, progress.Stage);
        Assert.Equal(3, progress.Completed);
        Assert.Equal(6, progress.Total);
        Assert.Equal(2, progress.Failed);
        Assert.Equal("ST-003-persist", progress.CurrentTitle);

        var reasons = progress.States
            .Where(state => state.Status == SubtaskStatus.Failed)
            .Select(state => $"{state.Title}: {state.FailReason}");

        Assert.Equal(
            [
                "ST-002-render: status.json meldete failed",
                "ST-004-verify: subtask.md fehlt oder ist leer",
            ],
            reasons);
    }

    // Requirement 3.2: before phase 4 there is nothing to show, so the idle payload is constructible
    // without a current title and without any states.
    [Fact]
    public void SubtaskProgress_SupportsAnIdlePayloadWithoutACurrentTitle()
    {
        var progress = new SubtaskProgress(SubtaskStage.Idle, 0, 0, 0, null, []);

        Assert.Null(progress.CurrentTitle);
        Assert.Empty(progress.States);
    }

    [Fact]
    public void SubtaskProgress_RejectsAMissingStateList()
    {
        Assert.Throws<ArgumentNullException>(
            () => new SubtaskProgress(SubtaskStage.Running, 0, 1, 0, "ST-001-parse", null!));
    }

    // The task's explicit constraint: the state list is exposed as a read-only collection, never as
    // a public mutable list (the CA1002/CA2227 convention this repository builds with).
    [Fact]
    public void StateLists_AreExposedAsReadOnlyCollections()
    {
        foreach (var property in new[]
                 {
                     typeof(SubtaskSnapshot).GetProperty(nameof(SubtaskSnapshot.States)),
                     typeof(SubtaskProgress).GetProperty(nameof(SubtaskProgress.States)),
                 })
        {
            Assert.NotNull(property);
            Assert.Equal(typeof(IReadOnlyList<SubtaskState>), property.PropertyType);
            Assert.Null(property.SetMethod);
        }
    }

    // The counts have to keep matching the states. A caller that hands in a still-mutable collection
    // and then changes it must not be able to drive States and Total apart.
    [Fact]
    public void SubtaskSnapshot_IsNotAffectedByLaterMutationOfTheSuppliedCollection()
    {
        var backing = new Collection<SubtaskState>([.. Known]);

        var snapshot = new SubtaskSnapshot(backing);
        backing.Add(new SubtaskState("ST-007-late", SubtaskStatus.Failed, "zu spät"));
        backing.RemoveAt(0);

        Assert.Equal(6, snapshot.States.Count);
        Assert.Equal(6, snapshot.Total);
        Assert.Equal(3, snapshot.Completed);
        Assert.Equal(2, snapshot.Failed);
        Assert.Equal("ST-001-parse", snapshot.States[0].Title);
    }
}
