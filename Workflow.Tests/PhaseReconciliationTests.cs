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
/// Every fixture here is deliberately asymmetric. The five phases carry three different recorded
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
    // Fixture. The four phases that leave a trace on disk get their artefacts written by default,
    // so a test that demotes does so for exactly the one reason it names.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// A journal with all five phases Completed and the spec, plan, simplifier report and review on disk.
    /// </summary>
    /// <remarks>
    /// The done-marker is deliberately NOT written: it is the single artefact under test, and a
    /// subtask task never has one (requirement 4.3).
    /// </remarks>
    private TaskState CompletedJournal()
    {
        File.WriteAllText(_paths.SpecAbsolute, "Spezifikation", Utf8);
        File.WriteAllText(_paths.PlanAbsolute, "Plan", Utf8);
        File.WriteAllText(_paths.SimplifiedAbsolute, "Vereinfacht", Utf8);
        File.WriteAllText(_paths.ReviewAbsolute, "Review", Utf8);

        return new TaskState
        {
            TaskDescription = "Beschreibung",
            CreatedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow,
            Phases = new Collection<TaskPhaseState>
            {
                new(WorkflowPhase.Specification, PhaseStatus.Completed, DateTimeOffset.UtcNow),
                new(WorkflowPhase.Simplifier, PhaseStatus.Completed, DateTimeOffset.UtcNow),
                new(WorkflowPhase.Review, PhaseStatus.Completed, DateTimeOffset.UtcNow),
                new(WorkflowPhase.ResolveReview, PhaseStatus.Completed, DateTimeOffset.UtcNow),
                new(WorkflowPhase.Implementation, PhaseStatus.Completed, DateTimeOffset.UtcNow),
            },
        };
    }

    private void WriteDoneMarker() => File.WriteAllText(_paths.DoneAbsolute, "fertig", Utf8);

    /// <summary>The simplifier's report, written at minute 20 - after the specification, before the review.</summary>
    private void WriteSimplifierReport() => WriteAt(_paths.SimplifiedAbsolute, "Vereinfacht", 20);

    /// <summary>A fixed instant, <paramref name="minute"/> minutes into the fixture's day.</summary>
    private static DateTimeOffset Minute(int minute) =>
        new DateTimeOffset(2026, 10, 1, 15, 0, 0, TimeSpan.Zero).AddMinutes(minute);

    /// <summary>Writes an artefact and pins its last-write time to <see cref="Minute"/>.</summary>
    private static void WriteAt(string path, string content, int minute)
    {
        File.WriteAllText(path, content, Utf8);
        File.SetLastWriteTimeUtc(path, Minute(minute).UtcDateTime);
    }

    /// <summary>
    /// A journal with the five given outcomes and no artefact written. A Completed phase carries a
    /// completion time that lies just after the artefacts the promotion fixtures write for it:
    /// specification 12, simplifier 21, review 31, resolve-review 45, implementation 91.
    /// </summary>
    private static TaskState JournalOf(
        PhaseStatus specification,
        PhaseStatus simplifier,
        PhaseStatus review,
        PhaseStatus resolveReview,
        PhaseStatus implementation)
    {
        static TaskPhaseState Entry(WorkflowPhase phase, PhaseStatus status, int minute) =>
            new(phase, status, status == PhaseStatus.Completed ? Minute(minute) : null);

        return new TaskState
        {
            TaskDescription = "Beschreibung",
            CreatedUtc = Minute(0),
            UpdatedUtc = Minute(0),
            Phases = new Collection<TaskPhaseState>
            {
                Entry(WorkflowPhase.Specification, specification, 12),
                Entry(WorkflowPhase.Simplifier, simplifier, 21),
                Entry(WorkflowPhase.Review, review, 31),
                Entry(WorkflowPhase.ResolveReview, resolveReview, 45),
                Entry(WorkflowPhase.Implementation, implementation, 91),
            },
        };
    }

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
    /// <c>Task abschliessen</c> on a normal task writes no done-marker; the explicit override is what
    /// keeps the task closed across a restart. The rule switch is the demotion's only: the evidence
    /// classification still says the task is not in subtask mode.
    /// </summary>
    [Fact]
    public void Reconcile_NormalTaskCompletedManuallyWithoutTheDoneMarker_KeepsImplementationCompleted()
    {
        var state = CompletedJournal();
        state.ImplementationCompletedManually = true;

        Assert.False(PhaseReconciliation.Reconcile(_paths, state));
        Assert.Equal(PhaseStatus.Completed, StatusOf(state, WorkflowPhase.Implementation));
        Assert.Null(PhaseReconciliation.FirstIncomplete(state));
        Assert.Equal(SubtaskEvidence.NotApplicable, PhaseReconciliation.Evaluate(_paths, state));
    }

    /// <summary>The override covers Implementation only; it never shields the phases before it.</summary>
    [Fact]
    public void Reconcile_NormalTaskCompletedManuallyMissingItsSpecification_DemotesFromSpecificationOnwards()
    {
        var state = CompletedJournal();
        state.ImplementationCompletedManually = true;
        File.Delete(_paths.SpecAbsolute);

        Assert.True(PhaseReconciliation.Reconcile(_paths, state));
        Assert.Equal(PhaseStatus.Pending, StatusOf(state, WorkflowPhase.Specification));
        Assert.Equal(PhaseStatus.Pending, StatusOf(state, WorkflowPhase.Implementation));
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
    /// Unknown evidence never promotes. Promotion needs a phase's completion condition to hold
    /// positively on disk; an index that cannot be read is <see cref="SubtaskEvidence.Unavailable"/>,
    /// which retains a Completed phase (requirement 4.6) and equally leaves a Pending one pending.
    /// 4.6's retention is pinned by <see cref="Reconcile_SubtaskTaskWithAMalformedIndex_KeepsTheRecordedPhaseState"/>
    /// and its siblings, which start from a Completed phase.
    /// </summary>
    [Fact]
    public void Reconcile_SubtaskTaskWithAnUnreadableIndexAndAPendingPhase_DoesNotPromoteIt()
    {
        var state = CompletedJournal();
        state.SubtasksEnabled = true;
        state.WorkflowDirectory = _tracking;
        state.Phases[(int)WorkflowPhase.Implementation] = new TaskPhaseState(WorkflowPhase.Implementation, PhaseStatus.Pending, null);

        Assert.False(PhaseReconciliation.Reconcile(_paths, state));
        Assert.Equal(PhaseStatus.Pending, StatusOf(state, WorkflowPhase.Implementation));
    }

    // -----------------------------------------------------------------------------------------
    // Promotion. The application can die between a CLI writing a phase's artefacts and the
    // orchestrator recording Completed; the journal then claims less than the disk proves. A phase
    // is promoted when its completion condition holds on disk AND its evidence is not older than
    // the previous phase's completion, so a leftover from an earlier cycle never counts. Every
    // timestamp is fixed, so no test depends on the coarse file-system clock.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// The Telematik_HTML case: spec and plan were written, the application hung and was killed
    /// before it recorded the phase, and recovery used to re-run phase 1 with a watcher that was
    /// already satisfied. The next phase is the simplifier, which has not left its report yet.
    /// </summary>
    [Fact]
    public void Reconcile_SpecificationActiveWithSpecAndPlanOnDisk_PromotesSpecificationAndResumesAtSimplifier()
    {
        // Arrange
        WriteAt(_paths.SpecAbsolute, "Spezifikation", 10);
        WriteAt(_paths.PlanAbsolute, "Plan", 11);
        var state = JournalOf(PhaseStatus.Active, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending);

        // Act
        var changed = PhaseReconciliation.Reconcile(_paths, state);

        // Assert
        Assert.True(changed);
        Assert.Equal(PhaseStatus.Completed, StatusOf(state, WorkflowPhase.Specification));
        Assert.Equal(Minute(11), state.Phases[(int)WorkflowPhase.Specification].CompletedUtc);
        Assert.Equal(PhaseStatus.Pending, StatusOf(state, WorkflowPhase.Simplifier));
        Assert.Equal(PhaseStatus.Pending, StatusOf(state, WorkflowPhase.Review));
        Assert.Equal(WorkflowPhase.Simplifier, PhaseReconciliation.FirstIncomplete(state));
    }

    [Fact]
    public void Reconcile_ReviewActiveWithAFreshReview_PromotesReviewAndResumesAtResolveReview()
    {
        // Arrange
        WriteAt(_paths.SpecAbsolute, "Spezifikation", 10);
        WriteAt(_paths.PlanAbsolute, "Plan", 11);
        WriteSimplifierReport();
        WriteAt(_paths.ReviewAbsolute, "Review", 30);
        var state = JournalOf(PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Active, PhaseStatus.Pending, PhaseStatus.Pending);

        // Act
        var changed = PhaseReconciliation.Reconcile(_paths, state);

        // Assert
        Assert.True(changed);
        Assert.Equal(PhaseStatus.Completed, StatusOf(state, WorkflowPhase.Review));
        Assert.Equal(Minute(30), state.Phases[(int)WorkflowPhase.Review].CompletedUtc);
        Assert.Equal(WorkflowPhase.ResolveReview, PhaseReconciliation.FirstIncomplete(state));
    }

    /// <summary>
    /// A review written before the current specification completed reviews an older spec. It is a
    /// leftover, not evidence, so phase 2 must run again.
    /// </summary>
    [Fact]
    public void Reconcile_ReviewOlderThanSpecificationCompletion_IsNotPromoted()
    {
        // Arrange
        WriteAt(_paths.ReviewAbsolute, "alte Review", 5);
        WriteAt(_paths.SpecAbsolute, "Spezifikation", 10);
        WriteAt(_paths.PlanAbsolute, "Plan", 11);
        WriteSimplifierReport();
        var state = JournalOf(PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending);

        // Act
        var changed = PhaseReconciliation.Reconcile(_paths, state);

        // Assert
        Assert.False(changed);
        Assert.Equal(PhaseStatus.Pending, StatusOf(state, WorkflowPhase.Review));
        Assert.Equal(WorkflowPhase.Review, PhaseReconciliation.FirstIncomplete(state));
    }

    /// <summary>
    /// Phase 3 leaves no artefact of its own. It is promoted under the same AllContentChanged rule
    /// its live watcher applies: spec AND plan both rewritten after the review.
    /// </summary>
    [Fact]
    public void Reconcile_SpecAndPlanRewrittenAfterReview_PromotesResolveReview()
    {
        // Arrange
        WriteAt(_paths.ReviewAbsolute, "Review", 30);
        WriteAt(_paths.SpecAbsolute, "Spezifikation, ueberarbeitet", 40);
        WriteAt(_paths.PlanAbsolute, "Plan, ueberarbeitet", 41);
        WriteSimplifierReport();
        var state = JournalOf(PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Active, PhaseStatus.Pending);

        // Act
        var changed = PhaseReconciliation.Reconcile(_paths, state);

        // Assert
        Assert.True(changed);
        Assert.Equal(PhaseStatus.Completed, StatusOf(state, WorkflowPhase.ResolveReview));
        Assert.Equal(Minute(41), state.Phases[(int)WorkflowPhase.ResolveReview].CompletedUtc);
        Assert.Equal(WorkflowPhase.Implementation, PhaseReconciliation.FirstIncomplete(state));
    }

    [Fact]
    public void Reconcile_OnlySpecRewrittenAfterReview_DoesNotPromoteResolveReview()
    {
        // Arrange
        WriteAt(_paths.PlanAbsolute, "Plan", 11);
        WriteAt(_paths.ReviewAbsolute, "Review", 30);
        WriteAt(_paths.SpecAbsolute, "Spezifikation, ueberarbeitet", 40);
        WriteSimplifierReport();
        var state = JournalOf(PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Active, PhaseStatus.Pending);

        // Act
        var changed = PhaseReconciliation.Reconcile(_paths, state);

        // Assert
        Assert.False(changed);
        Assert.Equal(PhaseStatus.Active, StatusOf(state, WorkflowPhase.ResolveReview));
        Assert.Equal(WorkflowPhase.ResolveReview, PhaseReconciliation.FirstIncomplete(state));
    }

    [Fact]
    public void Reconcile_ImplementationActiveWithFreshDoneMarker_PromotesAndFirstIncompleteIsNull()
    {
        // Arrange
        WriteAt(_paths.SpecAbsolute, "Spezifikation", 40);
        WriteAt(_paths.PlanAbsolute, "Plan", 41);
        WriteAt(_paths.ReviewAbsolute, "Review", 30);
        WriteAt(_paths.DoneAbsolute, "fertig", 90);
        WriteSimplifierReport();
        var state = JournalOf(PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Active);

        // Act
        var changed = PhaseReconciliation.Reconcile(_paths, state);

        // Assert
        Assert.True(changed);
        Assert.Equal(PhaseStatus.Completed, StatusOf(state, WorkflowPhase.Implementation));
        Assert.Equal(Minute(90), state.Phases[(int)WorkflowPhase.Implementation].CompletedUtc);
        Assert.Null(PhaseReconciliation.FirstIncomplete(state));
    }

    [Fact]
    public void Reconcile_DoneMarkerOlderThanResolveReviewCompletion_IsNotPromoted()
    {
        // Arrange
        WriteAt(_paths.DoneAbsolute, "fertig, voriger Durchlauf", 20);
        WriteAt(_paths.SpecAbsolute, "Spezifikation", 40);
        WriteAt(_paths.PlanAbsolute, "Plan", 41);
        WriteAt(_paths.ReviewAbsolute, "Review", 30);
        WriteSimplifierReport();
        var state = JournalOf(PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Active);

        // Act
        var changed = PhaseReconciliation.Reconcile(_paths, state);

        // Assert
        Assert.False(changed);
        Assert.Equal(WorkflowPhase.Implementation, PhaseReconciliation.FirstIncomplete(state));
    }

    /// <summary>
    /// Promotion walks the catalogue in order and stops at the first phase it cannot prove: a
    /// review on disk says nothing while the specification it reviewed is missing.
    /// </summary>
    [Fact]
    public void Reconcile_ReviewOnDiskButSpecificationMissing_PromotesNothing()
    {
        // Arrange
        WriteAt(_paths.PlanAbsolute, "Plan", 11);
        WriteAt(_paths.ReviewAbsolute, "Review", 30);
        var state = JournalOf(PhaseStatus.Active, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending);

        // Act
        var changed = PhaseReconciliation.Reconcile(_paths, state);

        // Assert
        Assert.False(changed);
        Assert.Equal(PhaseStatus.Active, StatusOf(state, WorkflowPhase.Specification));
        Assert.Equal(PhaseStatus.Pending, StatusOf(state, WorkflowPhase.Simplifier));
        Assert.Equal(PhaseStatus.Pending, StatusOf(state, WorkflowPhase.Review));
        Assert.Equal(WorkflowPhase.Specification, PhaseReconciliation.FirstIncomplete(state));
    }

    /// <summary>
    /// The subtask rule promotes on the same evidence that keeps a Completed phase: a freshly read
    /// ledger whose every entry is complete. The ledger carries no single timestamp, so the
    /// promoted entry has none either.
    /// </summary>
    [Fact]
    public void Reconcile_SubtaskTaskActiveWithACompleteLedger_PromotesImplementation()
    {
        // Arrange
        var state = CompletedJournal();
        state.SubtasksEnabled = true;
        state.WorkflowDirectory = _tracking;
        state.Phases[(int)WorkflowPhase.Implementation] = new TaskPhaseState(WorkflowPhase.Implementation, PhaseStatus.Active, null);
        WriteLedger(["ST-001-lesen", "ST-002-pruefen", "ST-003-schreiben"], complete: 3);

        // Act
        var changed = PhaseReconciliation.Reconcile(_paths, state);

        // Assert
        Assert.True(changed);
        Assert.Equal(PhaseStatus.Completed, StatusOf(state, WorkflowPhase.Implementation));
        Assert.Null(state.Phases[(int)WorkflowPhase.Implementation].CompletedUtc);
        Assert.Null(PhaseReconciliation.FirstIncomplete(state));
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

    /// <summary>
    /// Pins the completion predicate itself, now that one <c>TryRead</c> answers both questions.
    /// The fixture is asymmetric in every dimension the classification could read - six entries,
    /// three complete, two failed, one pending - so a mutant that completes on "no failures", on
    /// "some complete", or on the mere readability of the index fails here.
    /// </summary>
    [Fact]
    public void Evaluate_SubtaskTaskWithCompleteFailedAndPendingEntries_ReportsIncomplete()
    {
        var state = CompletedJournal();
        state.SubtasksEnabled = true;
        state.WorkflowDirectory = _tracking;

        var tracking = TrackingPaths();
        string[] titles =
        [
            "ST-001-lesen", "ST-002-pruefen", "ST-003-schreiben",
            "ST-004-testen", "ST-005-melden", "ST-006-abschliessen",
        ];

        File.WriteAllText(
            tracking.ResultAbsolute,
            $$"""{ "version": 1, "subtasks": {{System.Text.Json.JsonSerializer.Serialize(titles)}} }""",
            Utf8);

        for (var i = 0; i < titles.Length; i++)
        {
            Directory.CreateDirectory(tracking.SubtaskDirectory(titles[i]));
            File.WriteAllText(tracking.SubtaskMarkdown(titles[i]), "Beschreibung", Utf8);
            File.WriteAllText(
                tracking.SubtaskStatusFile(titles[i]),
                i < 3 ? """{ "status": "complete" }"""
                    : i < 5 ? """{ "status": "failed", "failreason": "Grund" }"""
                    : """{ "status": "pending" }""",
                Utf8);
        }

        Assert.Equal(SubtaskEvidence.Incomplete, PhaseReconciliation.Evaluate(_paths, state));
        Assert.True(PhaseReconciliation.Reconcile(_paths, state));
        Assert.Equal(PhaseStatus.Pending, StatusOf(state, WorkflowPhase.Implementation));
    }

    /// <summary>
    /// The readability answer and the completion answer come from one read, so an index that
    /// cannot be parsed reports <see cref="SubtaskEvidence.Unavailable"/> and never
    /// <see cref="SubtaskEvidence.Incomplete"/>. Collapsing the two into "not all complete" would
    /// demote a finished task whose tracking repository was momentarily unreadable.
    /// </summary>
    [Fact]
    public void Evaluate_SubtaskTaskWhoseIndexCannotBeParsed_ReportsUnavailableAndNeverIncomplete()
    {
        var state = CompletedJournal();
        state.SubtasksEnabled = true;
        state.WorkflowDirectory = _tracking;

        var tracking = TrackingPaths();
        File.WriteAllText(tracking.ResultAbsolute, """{ "subtasks": [ """, Utf8);

        Assert.Equal(SubtaskEvidence.Unavailable, PhaseReconciliation.Evaluate(_paths, state));
        Assert.False(PhaseReconciliation.Reconcile(_paths, state));
        Assert.Equal(PhaseStatus.Completed, StatusOf(state, WorkflowPhase.Implementation));
    }

    [Fact]
    public void Evaluate_RejectsNullArguments()
    {
        var state = CompletedJournal();

        Assert.Throws<ArgumentNullException>(() => PhaseReconciliation.Evaluate(null!, state));
        Assert.Throws<ArgumentNullException>(() => PhaseReconciliation.Evaluate(_paths, null!));
    }

    // -----------------------------------------------------------------------------------------
    // Workflow_LoadFix spec section 7.2: a skipped phase is done and never evidence-checked
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Specification completed at minute 12; simplifier and both review phases skipped at minute 20,
    /// as <c>Implementierung beginnen</c> records them; implementation as given.
    /// </summary>
    private static TaskState SkippedReviewJournal(PhaseStatus implementation)
    {
        var state = JournalOf(PhaseStatus.Completed, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending, implementation);
        state.Phases[(int)WorkflowPhase.Simplifier] = new TaskPhaseState(WorkflowPhase.Simplifier, PhaseStatus.Skipped, Minute(20));
        state.Phases[(int)WorkflowPhase.Review] = new TaskPhaseState(WorkflowPhase.Review, PhaseStatus.Skipped, Minute(20));
        state.Phases[(int)WorkflowPhase.ResolveReview] = new TaskPhaseState(WorkflowPhase.ResolveReview, PhaseStatus.Skipped, Minute(20));
        return state;
    }

    [Fact]
    public void Reconcile_SkippedReviewPhasesWithoutAReview_KeepsThemAndResumesAtImplementation()
    {
        // Arrange - no review file at all; a Completed review would be demoted here.
        WriteAt(_paths.SpecAbsolute, "Spezifikation", 10);
        WriteAt(_paths.PlanAbsolute, "Plan", 11);
        var state = SkippedReviewJournal(PhaseStatus.Pending);

        // Act
        var changed = PhaseReconciliation.Reconcile(_paths, state);

        // Assert
        Assert.False(changed);
        Assert.Equal(PhaseStatus.Completed, StatusOf(state, WorkflowPhase.Specification));
        Assert.Equal(PhaseStatus.Skipped, StatusOf(state, WorkflowPhase.Simplifier));
        Assert.Equal(PhaseStatus.Skipped, StatusOf(state, WorkflowPhase.Review));
        Assert.Equal(PhaseStatus.Skipped, StatusOf(state, WorkflowPhase.ResolveReview));
        Assert.Equal(WorkflowPhase.Implementation, PhaseReconciliation.FirstIncomplete(state));
    }

    [Fact]
    public void Reconcile_SpecificationMissingAfterASkip_DemotesTheSkippedPhasesToo()
    {
        // Arrange - neither spec nor plan on disk: the skip has nothing left to stand on.
        var state = SkippedReviewJournal(PhaseStatus.Pending);

        // Act
        var changed = PhaseReconciliation.Reconcile(_paths, state);

        // Assert
        Assert.True(changed);
        Assert.All(state.Phases, entry => Assert.Equal(PhaseStatus.Pending, entry.Status));
        Assert.Equal(WorkflowPhase.Specification, PhaseReconciliation.FirstIncomplete(state));
    }

    [Fact]
    public void Reconcile_DoneMarkerWrittenAfterTheSkip_PromotesImplementation()
    {
        // Arrange
        WriteAt(_paths.SpecAbsolute, "Spezifikation", 10);
        WriteAt(_paths.PlanAbsolute, "Plan", 11);
        WriteAt(_paths.DoneAbsolute, "fertig", 30);
        var state = SkippedReviewJournal(PhaseStatus.Active);

        // Act
        var changed = PhaseReconciliation.Reconcile(_paths, state);

        // Assert
        Assert.True(changed);
        Assert.Equal(PhaseStatus.Completed, StatusOf(state, WorkflowPhase.Implementation));
        Assert.Null(PhaseReconciliation.FirstIncomplete(state));
    }

    [Fact]
    public void Reconcile_DoneMarkerOlderThanTheSkip_IsNotPromoted()
    {
        // Arrange - a done marker from before the skip belongs to an earlier cycle.
        WriteAt(_paths.SpecAbsolute, "Spezifikation", 10);
        WriteAt(_paths.PlanAbsolute, "Plan", 11);
        WriteAt(_paths.DoneAbsolute, "alt", 15);
        var state = SkippedReviewJournal(PhaseStatus.Pending);

        // Act
        var changed = PhaseReconciliation.Reconcile(_paths, state);

        // Assert
        Assert.False(changed);
        Assert.Equal(PhaseStatus.Pending, StatusOf(state, WorkflowPhase.Implementation));
        Assert.Equal(WorkflowPhase.Implementation, PhaseReconciliation.FirstIncomplete(state));
    }

    // -----------------------------------------------------------------------------------------
    // Workflow_LoadFix spec sections 7.2 and 7.5 (AC-9): a hand-edited skip without a stamp keeps
    // the previous baseline instead of erasing it
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Spec and plan written at minutes 10 and 11, simplifier and both review phases skipped without a
    /// stamp, implementation pending. The specification is completed at minute 12, or without a stamp
    /// as well.
    /// </summary>
    private TaskState UnstampedSkipJournal(bool stampSpecification)
    {
        WriteAt(_paths.SpecAbsolute, "Spezifikation", 10);
        WriteAt(_paths.PlanAbsolute, "Plan", 11);

        var state = JournalOf(PhaseStatus.Completed, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending);
        if (!stampSpecification)
        {
            state.Phases[(int)WorkflowPhase.Specification] = new TaskPhaseState(WorkflowPhase.Specification, PhaseStatus.Completed, null);
        }

        state.Phases[(int)WorkflowPhase.Simplifier] = new TaskPhaseState(WorkflowPhase.Simplifier, PhaseStatus.Skipped, null);
        state.Phases[(int)WorkflowPhase.Review] = new TaskPhaseState(WorkflowPhase.Review, PhaseStatus.Skipped, null);
        state.Phases[(int)WorkflowPhase.ResolveReview] = new TaskPhaseState(WorkflowPhase.ResolveReview, PhaseStatus.Skipped, null);
        return state;
    }

    [Fact]
    public void Reconcile_SkipsWithoutAStampAndADoneMarkerOlderThanPhaseOne_DoesNotPromoteImplementation()
    {
        // Arrange - the unstamped skips keep phase 1's minute 12 as the baseline.
        var state = UnstampedSkipJournal(stampSpecification: true);
        WriteAt(_paths.DoneAbsolute, "alt", 11);

        // Act
        var changed = PhaseReconciliation.Reconcile(_paths, state);

        // Assert
        Assert.False(changed);
        Assert.Equal(PhaseStatus.Pending, StatusOf(state, WorkflowPhase.Implementation));
        Assert.Equal(WorkflowPhase.Implementation, PhaseReconciliation.FirstIncomplete(state));
    }

    [Fact]
    public void Reconcile_SkipsWithoutAStampAndADoneMarkerNewerThanPhaseOne_PromotesImplementation()
    {
        // Arrange
        var state = UnstampedSkipJournal(stampSpecification: true);
        WriteAt(_paths.DoneAbsolute, "fertig", 30);

        // Act
        var changed = PhaseReconciliation.Reconcile(_paths, state);

        // Assert
        Assert.True(changed);
        Assert.Equal(PhaseStatus.Completed, StatusOf(state, WorkflowPhase.Implementation));
        Assert.Null(PhaseReconciliation.FirstIncomplete(state));
    }

    [Fact]
    public void Reconcile_NoStampsAtAll_AnyDoneMarkerPromotesImplementation()
    {
        // Arrange - the documented residual risk of spec 7.5: without any stamp there is no
        // baseline, so even a done marker older than the spec counts.
        var state = UnstampedSkipJournal(stampSpecification: false);
        WriteAt(_paths.DoneAbsolute, "alt", 5);

        // Act
        var changed = PhaseReconciliation.Reconcile(_paths, state);

        // Assert
        Assert.True(changed);
        Assert.Equal(PhaseStatus.Completed, StatusOf(state, WorkflowPhase.Implementation));
        Assert.Null(PhaseReconciliation.FirstIncomplete(state));
    }

    // -----------------------------------------------------------------------------------------
    // The Simplifier ("Vereinfachen") between specification and review. Its report is its
    // completion marker. A journal written before the phase existed, or a folder whose review is
    // already on disk, passes it as Skipped instead of re-running it over reviewed documents.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Reconcile_SimplifierActiveWithAReportNewerThanTheSpecification_PromotesSimplifier()
    {
        // Arrange
        WriteAt(_paths.SpecAbsolute, "Spezifikation", 10);
        WriteAt(_paths.PlanAbsolute, "Plan", 11);
        WriteSimplifierReport();
        var state = JournalOf(PhaseStatus.Completed, PhaseStatus.Active, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending);

        // Act
        var changed = PhaseReconciliation.Reconcile(_paths, state);

        // Assert
        Assert.True(changed);
        Assert.Equal(PhaseStatus.Completed, StatusOf(state, WorkflowPhase.Simplifier));
        Assert.Equal(Minute(20), state.Phases[(int)WorkflowPhase.Simplifier].CompletedUtc);
        Assert.Equal(PhaseStatus.Pending, StatusOf(state, WorkflowPhase.Review));
        Assert.Equal(WorkflowPhase.Review, PhaseReconciliation.FirstIncomplete(state));
    }

    [Fact]
    public void Reconcile_SimplifierReportOlderThanTheSpecificationCompletion_IsNotPromoted()
    {
        // Arrange - a report from an earlier cycle describes a specification that has been rewritten.
        WriteAt(_paths.SimplifiedAbsolute, "Vereinfacht, voriger Durchlauf", 5);
        WriteAt(_paths.SpecAbsolute, "Spezifikation", 10);
        WriteAt(_paths.PlanAbsolute, "Plan", 11);
        var state = JournalOf(PhaseStatus.Completed, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending);

        // Act
        var changed = PhaseReconciliation.Reconcile(_paths, state);

        // Assert
        Assert.False(changed);
        Assert.Equal(PhaseStatus.Pending, StatusOf(state, WorkflowPhase.Simplifier));
        Assert.Equal(WorkflowPhase.Simplifier, PhaseReconciliation.FirstIncomplete(state));
    }

    [Fact]
    public void Reconcile_OnlySpecAndPlanOnDisk_StopsAtTheSimplifier()
    {
        // Arrange
        WriteAt(_paths.SpecAbsolute, "Spezifikation", 10);
        WriteAt(_paths.PlanAbsolute, "Plan", 11);
        var state = JournalOf(PhaseStatus.Completed, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending);

        // Act
        var changed = PhaseReconciliation.Reconcile(_paths, state);

        // Assert
        Assert.False(changed);
        Assert.Equal(PhaseStatus.Completed, StatusOf(state, WorkflowPhase.Specification));
        Assert.Equal(PhaseStatus.Pending, StatusOf(state, WorkflowPhase.Simplifier));
        Assert.Equal(PhaseStatus.Pending, StatusOf(state, WorkflowPhase.Review));
        Assert.Equal(WorkflowPhase.Simplifier, PhaseReconciliation.FirstIncomplete(state));
    }

    /// <summary>
    /// A journal from a build without the simplifier loads with it Pending between a completed
    /// specification and a completed review. It is passed as Skipped, stamped with the
    /// specification's completion, and every later phase keeps exactly what it recorded.
    /// </summary>
    [Fact]
    public void Reconcile_LegacyJournalWithTheReviewCompleted_SkipsTheSimplifierAtTheSpecificationStamp()
    {
        // Arrange
        WriteAt(_paths.SpecAbsolute, "Spezifikation", 10);
        WriteAt(_paths.PlanAbsolute, "Plan", 11);
        WriteAt(_paths.ReviewAbsolute, "Review", 30);
        var state = JournalOf(PhaseStatus.Completed, PhaseStatus.Pending, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Pending);

        // Act
        var changed = PhaseReconciliation.Reconcile(_paths, state);

        // Assert
        Assert.True(changed);
        Assert.Equal(PhaseStatus.Skipped, StatusOf(state, WorkflowPhase.Simplifier));
        Assert.Equal(Minute(12), state.Phases[(int)WorkflowPhase.Simplifier].CompletedUtc);
        Assert.Equal(
            new TaskPhaseState(WorkflowPhase.Specification, PhaseStatus.Completed, Minute(12)),
            state.Phases[(int)WorkflowPhase.Specification]);
        Assert.Equal(
            new TaskPhaseState(WorkflowPhase.Review, PhaseStatus.Completed, Minute(31)),
            state.Phases[(int)WorkflowPhase.Review]);
        Assert.Equal(
            new TaskPhaseState(WorkflowPhase.ResolveReview, PhaseStatus.Completed, Minute(45)),
            state.Phases[(int)WorkflowPhase.ResolveReview]);
        Assert.Equal(PhaseStatus.Pending, StatusOf(state, WorkflowPhase.Implementation));
        Assert.Equal(WorkflowPhase.Implementation, PhaseReconciliation.FirstIncomplete(state));
    }

    [Fact]
    public void Reconcile_LegacyFinishedJournal_SkipsTheSimplifierAndStaysFinished()
    {
        // Arrange
        WriteAt(_paths.SpecAbsolute, "Spezifikation", 10);
        WriteAt(_paths.PlanAbsolute, "Plan", 11);
        WriteAt(_paths.ReviewAbsolute, "Review", 30);
        WriteAt(_paths.DoneAbsolute, "fertig", 90);
        var state = JournalOf(PhaseStatus.Completed, PhaseStatus.Pending, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Completed);

        // Act
        var changed = PhaseReconciliation.Reconcile(_paths, state);

        // Assert
        Assert.True(changed);
        Assert.Equal(PhaseStatus.Skipped, StatusOf(state, WorkflowPhase.Simplifier));
        Assert.Equal(PhaseStatus.Completed, StatusOf(state, WorkflowPhase.Implementation));
        Assert.Null(PhaseReconciliation.FirstIncomplete(state));
    }

    [Fact]
    public void Reconcile_NoReportButAReviewWrittenAfterTheSpecification_SkipsTheSimplifierAndPromotesTheReview()
    {
        // Arrange - the review is on disk, the journal never saw it.
        WriteAt(_paths.SpecAbsolute, "Spezifikation", 10);
        WriteAt(_paths.PlanAbsolute, "Plan", 11);
        WriteAt(_paths.ReviewAbsolute, "Review", 30);
        var state = JournalOf(PhaseStatus.Completed, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending);

        // Act
        var changed = PhaseReconciliation.Reconcile(_paths, state);

        // Assert
        Assert.True(changed);
        Assert.Equal(PhaseStatus.Skipped, StatusOf(state, WorkflowPhase.Simplifier));
        Assert.Equal(Minute(12), state.Phases[(int)WorkflowPhase.Simplifier].CompletedUtc);
        Assert.Equal(PhaseStatus.Completed, StatusOf(state, WorkflowPhase.Review));
        Assert.Equal(Minute(30), state.Phases[(int)WorkflowPhase.Review].CompletedUtc);
        Assert.Equal(PhaseStatus.Pending, StatusOf(state, WorkflowPhase.ResolveReview));
        Assert.Equal(WorkflowPhase.ResolveReview, PhaseReconciliation.FirstIncomplete(state));
    }

    [Fact]
    public void Reconcile_NoReportAndAReviewOlderThanTheSpecification_DoesNotSkipTheSimplifier()
    {
        // Arrange - a leftover review of an earlier specification proves nothing about this cycle.
        WriteAt(_paths.ReviewAbsolute, "alte Review", 5);
        WriteAt(_paths.SpecAbsolute, "Spezifikation", 10);
        WriteAt(_paths.PlanAbsolute, "Plan", 11);
        var state = JournalOf(PhaseStatus.Completed, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending, PhaseStatus.Pending);

        // Act
        var changed = PhaseReconciliation.Reconcile(_paths, state);

        // Assert
        Assert.False(changed);
        Assert.Equal(PhaseStatus.Pending, StatusOf(state, WorkflowPhase.Simplifier));
        Assert.Equal(PhaseStatus.Pending, StatusOf(state, WorkflowPhase.Review));
        Assert.Equal(WorkflowPhase.Simplifier, PhaseReconciliation.FirstIncomplete(state));
    }

    /// <summary>
    /// A Completed simplifier whose report is gone is demoted with every later phase. The review on
    /// disk was written after the specification, so the promotion walk then passes the simplifier as
    /// Skipped and re-promotes the review from its own evidence.
    /// </summary>
    [Fact]
    public void Reconcile_CompletedSimplifierWhoseReportWasDeletedAndAProvableReview_DemotesThenSkipsIt()
    {
        // Arrange
        WriteAt(_paths.SpecAbsolute, "Spezifikation", 10);
        WriteAt(_paths.PlanAbsolute, "Plan", 11);
        WriteAt(_paths.ReviewAbsolute, "Review", 30);
        var state = JournalOf(PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Pending);

        // Act
        var changed = PhaseReconciliation.Reconcile(_paths, state);

        // Assert - the review keeps its disk time, not its old journal stamp: it was demoted and re-proven.
        Assert.True(changed);
        Assert.Equal(PhaseStatus.Completed, StatusOf(state, WorkflowPhase.Specification));
        Assert.Equal(PhaseStatus.Skipped, StatusOf(state, WorkflowPhase.Simplifier));
        Assert.Equal(Minute(12), state.Phases[(int)WorkflowPhase.Simplifier].CompletedUtc);
        Assert.Equal(PhaseStatus.Completed, StatusOf(state, WorkflowPhase.Review));
        Assert.Equal(Minute(30), state.Phases[(int)WorkflowPhase.Review].CompletedUtc);
        Assert.Equal(PhaseStatus.Pending, StatusOf(state, WorkflowPhase.ResolveReview));
        Assert.Equal(PhaseStatus.Pending, StatusOf(state, WorkflowPhase.Implementation));
        Assert.Equal(WorkflowPhase.ResolveReview, PhaseReconciliation.FirstIncomplete(state));
    }

    [Fact]
    public void Reconcile_CompletedSimplifierWhoseReportWasDeletedWithoutAReview_DemotesItAndEveryLaterPhase()
    {
        // Arrange
        WriteAt(_paths.SpecAbsolute, "Spezifikation", 10);
        WriteAt(_paths.PlanAbsolute, "Plan", 11);
        var state = JournalOf(PhaseStatus.Completed, PhaseStatus.Completed, PhaseStatus.Skipped, PhaseStatus.Skipped, PhaseStatus.Pending);

        // Act
        var changed = PhaseReconciliation.Reconcile(_paths, state);

        // Assert
        Assert.True(changed);
        Assert.Equal(PhaseStatus.Completed, StatusOf(state, WorkflowPhase.Specification));
        Assert.Equal(PhaseStatus.Pending, StatusOf(state, WorkflowPhase.Simplifier));
        Assert.Null(state.Phases[(int)WorkflowPhase.Simplifier].CompletedUtc);
        Assert.Equal(PhaseStatus.Pending, StatusOf(state, WorkflowPhase.Review));
        Assert.Equal(PhaseStatus.Pending, StatusOf(state, WorkflowPhase.ResolveReview));
        Assert.Equal(PhaseStatus.Pending, StatusOf(state, WorkflowPhase.Implementation));
        Assert.Equal(WorkflowPhase.Simplifier, PhaseReconciliation.FirstIncomplete(state));
    }
}
