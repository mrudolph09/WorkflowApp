using System.ComponentModel;
using Workflow.Models;
using Workflow.ViewModels;

namespace Workflow.Tests;

// Requirement 3 (3.1, 3.2, 3.3, 3.4, 3.7) and design "Presentation and Recovery" (issues 6a, 6b, 7).
public class SubtaskIndicatorViewModelTests
{
    // Deliberately asymmetric (Implementation Note 1.2): Total 7, Completed 4, Failed 2, Pending 1
    // are four PAIRWISE DISTINCT numbers, so an indicator that printed Failed where it meant
    // Completed - or counted Pending as Failed - produces a wrong string and is caught. The two
    // failed entries sit at index 1 and 5 with distinct reasons, which pins tooltip order and the
    // title-to-reason pairing. A symmetric fixture (2/2/1) previously let exactly such a swap pass.
    private static readonly SubtaskState[] MixedRun =
    [
        new("ST-001-parse", SubtaskStatus.Complete),
        new("ST-002-render", SubtaskStatus.Failed, "status.json meldete failed"),
        new("ST-003-persist", SubtaskStatus.Complete),
        new("ST-004-verify", SubtaskStatus.Pending),
        new("ST-005-ship", SubtaskStatus.Complete),
        new("ST-006-polish", SubtaskStatus.Failed, "subtask.md fehlt oder ist leer"),
        new("ST-007-release", SubtaskStatus.Complete),
    ];

    // A fresh decomposition: five entries, every one pending. Requirement 3.3 / design issue 1.
    private static readonly SubtaskState[] FreshDecomposition =
    [
        new("ST-001-parse", SubtaskStatus.Pending),
        new("ST-002-render", SubtaskStatus.Pending),
        new("ST-003-persist", SubtaskStatus.Pending),
        new("ST-004-verify", SubtaskStatus.Pending),
        new("ST-005-ship", SubtaskStatus.Pending),
    ];

    // Three entries, all complete - the only shape that may turn the indicator green (issue 6a).
    private static readonly SubtaskState[] EverythingComplete =
    [
        new("ST-001-parse", SubtaskStatus.Complete),
        new("ST-002-render", SubtaskStatus.Complete),
        new("ST-003-persist", SubtaskStatus.Complete),
    ];

    // Counts are derived from the states through SubtaskSnapshot, exactly as the ledger builds them,
    // so no test can hand the view model a payload whose counts contradict its own states.
    private static SubtaskProgress Progress(
        SubtaskStage stage,
        IReadOnlyList<SubtaskState> states,
        string? currentTitle = null)
    {
        var snapshot = new SubtaskSnapshot(states);
        return new SubtaskProgress(
            stage,
            snapshot.Completed,
            snapshot.Total,
            snapshot.Failed,
            currentTitle,
            snapshot.States);
    }

    // Implementation Note 1.2 made structural: the ignored dimension (pending) is pinned by
    // Total == Completed + Failed + pending, and all four numbers differ from one another.
    [Fact]
    public void Fixture_IsAsymmetricInEveryCounterTheIndicatorEvaluates()
    {
        var snapshot = new SubtaskSnapshot(MixedRun);
        var pending = snapshot.Total - snapshot.Completed - snapshot.Failed;

        Assert.Equal(7, snapshot.Total);
        Assert.Equal(4, snapshot.Completed);
        Assert.Equal(2, snapshot.Failed);
        Assert.Equal(1, pending);
        Assert.Equal(4, new[] { snapshot.Total, snapshot.Completed, snapshot.Failed, pending }.Distinct().Count());
    }

    // Requirement 3.1: the second indicator is labelled 'Subtasks'.
    [Fact]
    public void DisplayName_IsTheSubtaskLabel()
    {
        Assert.Equal("Subtasks", new SubtaskIndicatorViewModel().DisplayName);
    }

    // Requirement 3.2: while decomposition runs the indicator shows the decomposition message -
    // not a count, which would be meaningless before the index exists.
    [Fact]
    public void Apply_WhileDecomposing_ShowsTheDecompositionMessage()
    {
        var indicator = new SubtaskIndicatorViewModel();

        indicator.Apply(Progress(SubtaskStage.Decomposing, []));

        Assert.Equal("Zerlegung läuft…", indicator.ProgressText);
    }

    // Requirement 3.2: while the loop runs the indicator shows '{N} von {M}'.
    [Fact]
    public void Apply_WhileRunning_ShowsCompletedOfTotal()
    {
        var indicator = new SubtaskIndicatorViewModel();

        indicator.Apply(Progress(SubtaskStage.Running, MixedRun, "ST-004-verify"));

        Assert.Equal("4 von 7", indicator.ProgressText);
    }

    // Requirement 3.2: 'updating after each subtask'. A second payload must move the count and
    // must notify, or the UI would keep showing the first one.
    [Fact]
    public void Apply_AfterEachSubtask_UpdatesTheCountAndNotifies()
    {
        var indicator = new SubtaskIndicatorViewModel();
        indicator.Apply(Progress(SubtaskStage.Running, MixedRun));

        var changed = new List<string?>();
        ((INotifyPropertyChanged)indicator).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        indicator.Apply(Progress(
            SubtaskStage.Running,
            [.. MixedRun.Select(s => s with { Status = SubtaskStatus.Complete, FailReason = null })]));

        Assert.Equal("7 von 7", indicator.ProgressText);
        Assert.Contains(nameof(SubtaskIndicatorViewModel.ProgressText), changed);
    }

    // Requirement 3.3: the failed count appears alongside the progress text.
    [Fact]
    public void Apply_WithFailures_ShowsTheFailedCount()
    {
        var indicator = new SubtaskIndicatorViewModel();

        indicator.Apply(Progress(SubtaskStage.Running, MixedRun));

        Assert.True(indicator.HasFailures);
        Assert.Equal("2 fehlgeschlagen", indicator.FailedText);
    }

    // Requirement 3.3 / design issue 1: pending is not failed, so a freshly decomposed index shows
    // no failure text and no tooltip at all.
    [Fact]
    public void Apply_WithOnlyPendingEntries_ReportsNoFailures()
    {
        var indicator = new SubtaskIndicatorViewModel();

        indicator.Apply(Progress(SubtaskStage.Running, FreshDecomposition));

        Assert.False(indicator.HasFailures);
        Assert.Equal(string.Empty, indicator.FailedText);
        Assert.Null(indicator.FailureTooltip);
    }

    // Requirement 3.3: a later clean payload must clear the earlier failure text; stale failures
    // would keep claiming a failure that the disk no longer reports.
    [Fact]
    public void Apply_AfterFailuresAreResolved_ClearsTheFailureTextAndTooltip()
    {
        var indicator = new SubtaskIndicatorViewModel();
        indicator.Apply(Progress(SubtaskStage.Running, MixedRun));

        indicator.Apply(Progress(SubtaskStage.Running, EverythingComplete));

        Assert.False(indicator.HasFailures);
        Assert.Equal(string.Empty, indicator.FailedText);
        Assert.Null(indicator.FailureTooltip);
    }

    // Requirement 3.4: grey before phase 4. Nothing has been decomposed, so nothing is claimed.
    [Fact]
    public void Apply_WhileIdleWithoutSubtasks_IsGreyAndShowsZeroOfZero()
    {
        var indicator = new SubtaskIndicatorViewModel();

        indicator.Apply(Progress(SubtaskStage.Idle, []));

        Assert.Equal(PhaseStatus.Pending, indicator.Status);
        Assert.Equal("0 von 0", indicator.ProgressText);
    }

    // Requirement 3.4: yellow while running - decomposition is part of the run.
    [Fact]
    public void Apply_WhileDecomposing_IsYellow()
    {
        var indicator = new SubtaskIndicatorViewModel();

        indicator.Apply(Progress(SubtaskStage.Decomposing, []));

        Assert.Equal(PhaseStatus.Active, indicator.Status);
    }

    // Requirement 3.4: yellow while the loop runs.
    [Fact]
    public void Apply_WhileRunning_IsYellow()
    {
        var indicator = new SubtaskIndicatorViewModel();

        indicator.Apply(Progress(SubtaskStage.Running, MixedRun));

        Assert.Equal(PhaseStatus.Active, indicator.Status);
    }

    // Requirement 3.4 / design issue 6a: green requires Total > 0 AND Completed == Total.
    [Fact]
    public void Apply_WhenEveryEntryIsComplete_IsGreen()
    {
        var indicator = new SubtaskIndicatorViewModel();

        indicator.Apply(Progress(SubtaskStage.Running, EverythingComplete));

        Assert.Equal(PhaseStatus.Completed, indicator.Status);
        Assert.Equal("3 von 3", indicator.ProgressText);
    }

    // Requirement 3.4 / design issue 6a: 'the absence of failures alone shall not turn it green'.
    [Fact]
    public void Apply_WithNoFailuresButUnfinishedWork_IsNotGreen()
    {
        var indicator = new SubtaskIndicatorViewModel();

        indicator.Apply(Progress(SubtaskStage.Running, FreshDecomposition));

        Assert.NotEqual(PhaseStatus.Completed, indicator.Status);
        Assert.Equal(PhaseStatus.Active, indicator.Status);
        Assert.Equal("0 von 5", indicator.ProgressText);
    }

    // Requirement 3.4 / design issue 6a: the Total > 0 half of the green condition. An empty index
    // trivially satisfies Completed == Total and must still not be green.
    [Fact]
    public void Apply_WithAnEmptyIndex_IsNeverGreen()
    {
        var indicator = new SubtaskIndicatorViewModel();

        indicator.Apply(Progress(SubtaskStage.Running, []));

        Assert.NotEqual(PhaseStatus.Completed, indicator.Status);
    }

    // Requirement 3.4 / design issue 6b: a manually completed task keeps showing its real counts
    // instead of claiming green, because the colour is derived from the counts and nothing else.
    [Fact]
    public void Apply_WithPartialProgress_KeepsShowingTheRealCountsRatherThanGreen()
    {
        var indicator = new SubtaskIndicatorViewModel();

        indicator.Apply(Progress(SubtaskStage.Finished, MixedRun));

        Assert.Equal("4 von 7", indicator.ProgressText);
        Assert.NotEqual(PhaseStatus.Completed, indicator.Status);
    }

    // SubtaskStage.Finished, decided here (see the view model's remarks): a finished run is judged
    // by its counts, exactly like a running one - yellow while work is outstanding...
    [Fact]
    public void Apply_WhenFinishedWithFailures_StaysYellowAndKeepsTheFailedCount()
    {
        var indicator = new SubtaskIndicatorViewModel();

        indicator.Apply(Progress(SubtaskStage.Finished, MixedRun));

        Assert.Equal(PhaseStatus.Active, indicator.Status);
        Assert.Equal("2 fehlgeschlagen", indicator.FailedText);
    }

    // ...and green once every entry is complete, which is requirement 3.5's end state.
    [Fact]
    public void Apply_WhenFinishedWithEverythingComplete_IsGreen()
    {
        var indicator = new SubtaskIndicatorViewModel();

        indicator.Apply(Progress(SubtaskStage.Finished, EverythingComplete));

        Assert.Equal(PhaseStatus.Completed, indicator.Status);
    }

    // The task's observable: a decomposed but unstarted task - the shape LoadForResume seeds from
    // the ledger - shows zero of N, no failures, and is not green.
    //
    // The exact colour here is a DECISION, not something the task text settled: its observable asks
    // only for a "non-green" indicator, and requirement 3.4's "grey before phase 4" does not decide
    // this case, because decomposition itself happens inside phase 4. The authority relied on is
    // SubtaskStage.Idle's own documented contract in Workflow/Models/SubtaskStatus.cs:32-33 -
    // "Nothing has started; the indicator is grey" - a committed artifact of task 1.2. Asserting
    // grey rather than merely not-green is what pins Idle with Total > 0; NotEqual(Completed)
    // cannot tell grey from yellow and let a mutant decide the colour by the count instead of the
    // stage.
    [Fact]
    public void Apply_WhenDecomposedButUnstarted_ShowsZeroOfTotalWithoutFailuresAndIsGrey()
    {
        var indicator = new SubtaskIndicatorViewModel();

        indicator.Apply(Progress(SubtaskStage.Idle, FreshDecomposition));

        Assert.Equal("0 von 5", indicator.ProgressText);
        Assert.False(indicator.HasFailures);
        Assert.Null(indicator.FailureTooltip);
        Assert.Equal(PhaseStatus.Pending, indicator.Status);
    }

    // Requirement 3.7 / design issue 7: each failed entry's title AND reason, in index order,
    // taken from the States the payload carries.
    [Fact]
    public void Apply_OffersEachFailedEntrysTitleAndReasonInTheTooltip()
    {
        var indicator = new SubtaskIndicatorViewModel();

        indicator.Apply(Progress(SubtaskStage.Running, MixedRun));

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "ST-002-render: status.json meldete failed",
                "ST-006-polish: subtask.md fehlt oder ist leer"),
            indicator.FailureTooltip);
    }

    // Requirement 3.3 + 3.7: only failed entries belong in the tooltip. A pending or complete
    // entry appearing there would report a failure that does not exist.
    [Fact]
    public void Apply_TooltipOmitsCompleteAndPendingEntries()
    {
        var indicator = new SubtaskIndicatorViewModel();

        indicator.Apply(Progress(SubtaskStage.Running, MixedRun));

        var tooltip = Assert.IsType<string>(indicator.FailureTooltip);
        Assert.DoesNotContain("ST-001-parse", tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("ST-004-verify", tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("ST-007-release", tooltip, StringComparison.Ordinal);
    }

    // SubtaskState.FailReason is null when there is nothing to explain; the tooltip then names the
    // failed entry alone rather than printing a dangling separator.
    [Fact]
    public void Apply_WhenAFailedEntryHasNoReason_ShowsItsTitleAlone()
    {
        var indicator = new SubtaskIndicatorViewModel();

        indicator.Apply(Progress(
            SubtaskStage.Running,
            [new SubtaskState("ST-001-parse", SubtaskStatus.Complete),
             new SubtaskState("ST-002-render", SubtaskStatus.Failed)]));

        Assert.Equal("ST-002-render", indicator.FailureTooltip);
    }

    // Design, Presentation and Recovery: Reset returns the indicator to the state it has before
    // anything is known, so a reused tab cannot keep showing the previous run's numbers.
    [Fact]
    public void Reset_ReturnsTheIndicatorToItsGreyEmptyState()
    {
        var indicator = new SubtaskIndicatorViewModel();
        indicator.Apply(Progress(SubtaskStage.Running, MixedRun));

        indicator.Reset();

        Assert.Equal(PhaseStatus.Pending, indicator.Status);
        Assert.Equal("0 von 0", indicator.ProgressText);
        Assert.Equal(string.Empty, indicator.FailedText);
        Assert.False(indicator.HasFailures);
        Assert.Null(indicator.FailureTooltip);
    }

    // A new indicator is already in the reset state; nothing may be claimed before a payload.
    [Fact]
    public void NewIndicator_StartsGreyWithNoCountsAndNoFailures()
    {
        var indicator = new SubtaskIndicatorViewModel();

        Assert.Equal(PhaseStatus.Pending, indicator.Status);
        Assert.Equal("0 von 0", indicator.ProgressText);
        Assert.Equal(string.Empty, indicator.FailedText);
        Assert.False(indicator.HasFailures);
        Assert.Null(indicator.FailureTooltip);
    }

    [Fact]
    public void Apply_WithoutAProgressPayload_Throws()
    {
        var indicator = new SubtaskIndicatorViewModel();

        Assert.Throws<ArgumentNullException>(() => indicator.Apply(null!));
    }
}
