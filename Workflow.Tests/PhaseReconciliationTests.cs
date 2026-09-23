using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using Workflow.Models;

namespace Workflow.Tests;

/// <remarks>
/// <para>
/// Task 5.1: reconciliation reads its implementation-phase evidence from the subtask ledger for a
/// task whose journal records subtask mode, and from the normal done-marker for every other task.
/// The rule table is the design's resolved decision for issue 9 (requirements 4.3, 4.4, 4.6, 5.4).
/// </para>
/// <para>
/// Every fixture here is deliberately asymmetric. The four phases carry three different recorded
/// outcomes, the ledgers carry pairwise distinct complete/pending counts, and the done-marker is
/// staged independently of the ledger - so a mutant that swaps two evidence sources, or that
/// collapses the rule table into "never demote", fails rather than slips through.
/// </para>
/// </remarks>
public sealed class PhaseReconciliationTests : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private const string TaskName = "aufgabe-fuenf";

    private readonly string _root;
    private readonly string _workspace;
    private readonly string _tracking;
    private readonly TaskPaths _paths;

    public PhaseReconciliationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wf-recon-" + Guid.NewGuid().ToString("N"));
        _workspace = Path.Combine(_root, "arbeit");
        _tracking = Path.Combine(_root, "workflows");
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_tracking);

        _paths = new TaskPaths(_workspace, TaskName);
        Directory.CreateDirectory(_paths.TaskDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // -----------------------------------------------------------------------------------------
    // Fixture. The three phases that leave a trace on disk get their artefacts written by default,
    // so a test that demotes does so for exactly the one reason it names.
    // -----------------------------------------------------------------------------------------

    /// <summary>A journal with all four phases Completed and the spec, plan and review on disk.</summary>
    /// <remarks>
    /// The done-marker is deliberately NOT written: it is the single artefact under test, and a
    /// subtask task never has one (requirement 4.3).
    /// </remarks>
    private TaskState CompletedJournal()
    {
        File.WriteAllText(_paths.SpecAbsolute, "Spezifikation", Utf8);
        File.WriteAllText(_paths.PlanAbsolute, "Plan", Utf8);
        File.WriteAllText(_paths.ReviewAbsolute, "Review", Utf8);

        return new TaskState
        {
            TaskDescription = "Beschreibung",
            CreatedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow,
            Phases = new Collection<TaskPhaseState>
            {
                new(WorkflowPhase.Specification, PhaseStatus.Completed, DateTimeOffset.UtcNow),
                new(WorkflowPhase.Review, PhaseStatus.Completed, DateTimeOffset.UtcNow),
                new(WorkflowPhase.ResolveReview, PhaseStatus.Completed, DateTimeOffset.UtcNow),
                new(WorkflowPhase.Implementation, PhaseStatus.Completed, DateTimeOffset.UtcNow),
            },
        };
    }

    private void WriteDoneMarker() => File.WriteAllText(_paths.DoneAbsolute, "fertig", Utf8);

    private SubtaskPaths TrackingPaths()
    {
        var tracking = new SubtaskPaths(_tracking, TaskName);
        Directory.CreateDirectory(tracking.TaskDirectory);
        return tracking;
    }

    /// <summary>Writes an ordered index and marks the first <paramref name="complete"/> entries complete.</summary>
    /// <param name="titles">The entries, in index order.</param>
    /// <param name="complete">How many of them publish a <c>complete</c> status payload.</param>
    /// <param name="publishFlags">Whether each completed entry also publishes its session flag.</param>
    private void WriteLedger(string[] titles, int complete, bool publishFlags = true)
    {
        var tracking = TrackingPaths();
        File.WriteAllText(
            tracking.ResultAbsolute,
            $$"""{ "version": 1, "subtasks": {{System.Text.Json.JsonSerializer.Serialize(titles)}} }""",
            Utf8);

        for (var i = 0; i < titles.Length; i++)
        {
            var title = titles[i];
            Directory.CreateDirectory(tracking.SubtaskDirectory(title));

            // Every entry keeps a usable description: an entry without one is Failed for a reason
            // that has nothing to do with the rule under test.
            File.WriteAllText(tracking.SubtaskMarkdown(title), $"Beschreibung von {title}", Utf8);

            if (i >= complete)
            {
                continue;
            }

            File.WriteAllText(tracking.SubtaskStatusFile(title), """{ "status": "complete" }""", Utf8);

            if (publishFlags)
            {
                File.WriteAllText(tracking.SubtaskResultFile(title), """{ "finished": true }""", Utf8);
            }
        }
    }

    private static PhaseStatus StatusOf(TaskState state, WorkflowPhase phase) =>
        state.Phases.Single(entry => entry.Phase == phase).Status;

    // -----------------------------------------------------------------------------------------
    // Normal mode is untouched. These are the regression guard the task's third Observable names.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Reconcile_NormalTaskWithoutTheDoneMarker_DemotesImplementation()
    {
        var state = CompletedJournal();

        Assert.True(PhaseReconciliation.Reconcile(_paths, state));
        Assert.Equal(PhaseStatus.Pending, StatusOf(state, WorkflowPhase.Implementation));
        Assert.Equal(PhaseStatus.Completed, StatusOf(state, WorkflowPhase.Specification));
        Assert.Equal(PhaseStatus.Completed, StatusOf(state, WorkflowPhase.Review));
        Assert.Equal(PhaseStatus.Completed, StatusOf(state, WorkflowPhase.ResolveReview));
    }

    [Fact]
    public void Reconcile_NormalTaskWithTheDoneMarker_KeepsImplementationCompleted()
    {
        var state = CompletedJournal();
        WriteDoneMarker();

        Assert.False(PhaseReconciliation.Reconcile(_paths, state));
        Assert.Equal(PhaseStatus.Completed, StatusOf(state, WorkflowPhase.Implementation));
    }

    /// <summary>
    /// The journal fields alone must not switch the rule: a task whose tracking repository is fully
    /// complete but whose <c>SubtasksEnabled</c> is false is still a normal task and still demotes.
    /// </summary>
    [Fact]
    public void Reconcile_NormalTaskWithACompleteTrackingRepository_StillDemotesWithoutTheDoneMarker()
    {
        var state = CompletedJournal();
        state.WorkflowDirectory = _tracking;
        WriteLedger(["ST-001-lesen", "ST-002-pruefen", "ST-003-schreiben"], complete: 3);

        Assert.True(PhaseReconciliation.Reconcile(_paths, state));
        Assert.Equal(PhaseStatus.Pending, StatusOf(state, WorkflowPhase.Implementation));
        Assert.Equal(SubtaskEvidence.NotApplicable, PhaseReconciliation.Evaluate(_paths, state));
    }

    // -----------------------------------------------------------------------------------------
    // Requirement 4.3: a complete subtask task survives a restart with no done-marker anywhere.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Reconcile_SubtaskTaskWhoseLedgerIsComplete_KeepsImplementationCompleted()
    {
        var state = CompletedJournal();
        state.SubtasksEnabled = true;
        state.WorkflowDirectory = _tracking;
        WriteLedger(["ST-001-lesen", "ST-002-pruefen", "ST-003-schreiben"], complete: 3);

        Assert.False(PhaseReconciliation.Reconcile(_paths, state));
        Assert.Equal(PhaseStatus.Completed, StatusOf(state, WorkflowPhase.Implementation));
        Assert.Equal(SubtaskEvidence.AllComplete, PhaseReconciliation.Evaluate(_paths, state));
    }

    /// <summary>
    /// Requirement 4.4: a status payload that records completion is enough on recovery - the
    /// per-subtask session flag is not consulted at all, so an agent that died between publishing
    /// the payload and publishing the flag does not cost the task its completion.
    /// </summary>
    [Fact]
    public void Reconcile_SubtaskTaskCompleteWithoutSessionFlags_KeepsImplementationCompleted()
    {
        var state = CompletedJournal();
        state.SubtasksEnabled = true;
        state.WorkflowDirectory = _tracking;
        WriteLedger(["ST-001-lesen", "ST-002-pruefen"], complete: 2, publishFlags: false);

        Assert.False(PhaseReconciliation.Reconcile(_paths, state));
        Assert.Equal(PhaseStatus.Completed, StatusOf(state, WorkflowPhase.Implementation));
        Assert.Equal(SubtaskEvidence.AllComplete, PhaseReconciliation.Evaluate(_paths, state));
    }

    /// <summary>
    /// The done-marker is never a fallback for a subtask task, in either direction: a ledger that
    /// shows unfinished work demotes even though the marker is sitting on disk.
    /// </summary>
    [Fact]
    public void Reconcile_SubtaskTaskWithADoneMarkerButAnIncompleteLedger_DemotesImplementation()
    {
        var state = CompletedJournal();
        state.SubtasksEnabled = true;
        state.WorkflowDirectory = _tracking;
        WriteDoneMarker();
        WriteLedger(["ST-001-lesen", "ST-002-pruefen", "ST-003-schreiben", "ST-004-liefern"], complete: 1);

        Assert.True(PhaseReconciliation.Reconcile(_paths, state));
        Assert.Equal(PhaseStatus.Pending, StatusOf(state, WorkflowPhase.Implementation));
        Assert.Equal(SubtaskEvidence.Incomplete, PhaseReconciliation.Evaluate(_paths, state));
    }

    [Fact]
    public void Reconcile_SubtaskTaskWithAnIncompleteLedger_DemotesImplementation()
    {
        var state = CompletedJournal();
        state.SubtasksEnabled = true;
        state.WorkflowDirectory = _tracking;
        WriteLedger(["ST-001-lesen", "ST-002-pruefen", "ST-003-schreiben"], complete: 2);

        Assert.True(PhaseReconciliation.Reconcile(_paths, state));
        Assert.Equal(PhaseStatus.Pending, StatusOf(state, WorkflowPhase.Implementation));
    }

    /// <summary>Subtask mode changes the implementation rule only; the earlier phases keep theirs.</summary>
    [Fact]
    public void Reconcile_SubtaskTaskMissingItsSpecification_DemotesFromSpecificationOnwards()
    {
        var state = CompletedJournal();
        state.SubtasksEnabled = true;
        state.WorkflowDirectory = _tracking;
        WriteLedger(["ST-001-lesen", "ST-002-pruefen", "ST-003-schreiben"], complete: 3);
        File.Delete(_paths.SpecAbsolute);

        Assert.True(PhaseReconciliation.Reconcile(_paths, state));
        Assert.Equal(PhaseStatus.Pending, StatusOf(state, WorkflowPhase.Specification));
        Assert.Equal(PhaseStatus.Pending, StatusOf(state, WorkflowPhase.Implementation));
    }

    // -----------------------------------------------------------------------------------------
    // Requirement 5.4: a manual completion is honoured WITHOUT consulting the ledger. The fixture
    // stages a ledger that would demote, so consulting it is the observable failure.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Reconcile_SubtaskTaskCompletedManually_KeepsCompletionDespiteAnIncompleteLedger()
    {
        var state = CompletedJournal();
        state.SubtasksEnabled = true;
        state.WorkflowDirectory = _tracking;
        state.ImplementationCompletedManually = true;
        WriteLedger(["ST-001-lesen", "ST-002-pruefen", "ST-003-schreiben", "ST-004-liefern"], complete: 1);

        Assert.False(PhaseReconciliation.Reconcile(_paths, state));
        Assert.Equal(PhaseStatus.Completed, StatusOf(state, WorkflowPhase.Implementation));
        Assert.Equal(SubtaskEvidence.CompletedManually, PhaseReconciliation.Evaluate(_paths, state));
    }

    /// <summary>
    /// The override outranks a missing tracking repository too: nothing on disk is read at all.
    /// </summary>
    [Fact]
    public void Reconcile_SubtaskTaskCompletedManuallyWithABlankPath_KeepsCompletion()
    {
        var state = CompletedJournal();
        state.SubtasksEnabled = true;
        state.WorkflowDirectory = "   ";
        state.ImplementationCompletedManually = true;

        Assert.False(PhaseReconciliation.Reconcile(_paths, state));
        Assert.Equal(SubtaskEvidence.CompletedManually, PhaseReconciliation.Evaluate(_paths, state));
    }

    // -----------------------------------------------------------------------------------------
    // Requirement 4.6: an unusable stored path or an unreadable index retains the recorded state.
    // Unreadable is not unfinished, and it is not the done-marker's business either.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Reconcile_SubtaskTaskWithABlankTrackingPath_KeepsTheRecordedPhaseState(string? directory)
    {
        var state = CompletedJournal();
        state.SubtasksEnabled = true;
        state.WorkflowDirectory = directory;

        Assert.False(PhaseReconciliation.Reconcile(_paths, state));
        Assert.Equal(PhaseStatus.Completed, StatusOf(state, WorkflowPhase.Implementation));
        Assert.Equal(SubtaskEvidence.Unavailable, PhaseReconciliation.Evaluate(_paths, state));
    }

    [Fact]
    public void Reconcile_SubtaskTaskWithoutAnyTrackingRepository_KeepsTheRecordedPhaseState()
    {
        var state = CompletedJournal();
        state.SubtasksEnabled = true;
        state.WorkflowDirectory = Path.Combine(_root, "nicht-vorhanden");

        Assert.False(PhaseReconciliation.Reconcile(_paths, state));
        Assert.Equal(PhaseStatus.Completed, StatusOf(state, WorkflowPhase.Implementation));
        Assert.Equal(SubtaskEvidence.Unavailable, PhaseReconciliation.Evaluate(_paths, state));
    }

    [Fact]
    public void Reconcile_SubtaskTaskWithAMalformedIndex_KeepsTheRecordedPhaseState()
    {
        var state = CompletedJournal();
        state.SubtasksEnabled = true;
        state.WorkflowDirectory = _tracking;

        var tracking = TrackingPaths();
        File.WriteAllText(tracking.ResultAbsolute, """{ "subtasks": ["ST-001-lesen" """, Utf8);

        Assert.False(PhaseReconciliation.Reconcile(_paths, state));
        Assert.Equal(PhaseStatus.Completed, StatusOf(state, WorkflowPhase.Implementation));
        Assert.Equal(SubtaskEvidence.Unavailable, PhaseReconciliation.Evaluate(_paths, state));
    }

    /// <summary>
    /// The recorded state is retained, not promoted: an unreadable index leaves a phase that was
    /// already Pending exactly where it was.
    /// </summary>
    [Fact]
    public void Reconcile_SubtaskTaskWithAnUnreadableIndexAndAPendingPhase_DoesNotPromoteIt()
    {
        var state = CompletedJournal();
        state.SubtasksEnabled = true;
        state.WorkflowDirectory = _tracking;
        state.Phases[3] = new TaskPhaseState(WorkflowPhase.Implementation, PhaseStatus.Pending, null);

        Assert.False(PhaseReconciliation.Reconcile(_paths, state));
        Assert.Equal(PhaseStatus.Pending, StatusOf(state, WorkflowPhase.Implementation));
    }

    // -----------------------------------------------------------------------------------------
    // The classification itself, which is what carries the condition to the presentation layer.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Evaluate_NormalTask_ReportsNotApplicable()
    {
        var state = CompletedJournal();

        Assert.Equal(SubtaskEvidence.NotApplicable, PhaseReconciliation.Evaluate(_paths, state));
    }

    [Fact]
    public void Evaluate_SubtaskTaskWithPendingWork_ReportsIncomplete()
    {
        var state = CompletedJournal();
        state.SubtasksEnabled = true;
        state.WorkflowDirectory = _tracking;
        WriteLedger(["ST-001-lesen", "ST-002-pruefen", "ST-003-schreiben"], complete: 2);

        Assert.Equal(SubtaskEvidence.Incomplete, PhaseReconciliation.Evaluate(_paths, state));
    }

    [Fact]
    public void Evaluate_ReadsTheLedgerAfreshOnEveryCall()
    {
        var state = CompletedJournal();
        state.SubtasksEnabled = true;
        state.WorkflowDirectory = _tracking;
        WriteLedger(["ST-001-lesen", "ST-002-pruefen", "ST-003-schreiben"], complete: 2);

        Assert.Equal(SubtaskEvidence.Incomplete, PhaseReconciliation.Evaluate(_paths, state));

        var tracking = new SubtaskPaths(_tracking, TaskName);
        File.WriteAllText(
            tracking.SubtaskStatusFile("ST-003-schreiben"), """{ "status": "complete" }""", Utf8);

        Assert.Equal(SubtaskEvidence.AllComplete, PhaseReconciliation.Evaluate(_paths, state));
    }

    [Fact]
    public void Evaluate_RejectsNullArguments()
    {
        var state = CompletedJournal();

        Assert.Throws<ArgumentNullException>(() => PhaseReconciliation.Evaluate(null!, state));
        Assert.Throws<ArgumentNullException>(() => PhaseReconciliation.Evaluate(_paths, null!));
    }
}
