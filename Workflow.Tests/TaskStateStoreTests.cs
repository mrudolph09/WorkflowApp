using System.IO;
using Workflow.Models;
using Workflow.Services;

namespace Workflow.Tests;

public sealed class TaskStateStoreTests : IDisposable
{
    private readonly string _root;
    private readonly TaskPaths _paths;
    private readonly TaskStateStore _store = new();

    // The two review phases "Implementierung beginnen" skips (Workflow_LoadFix spec 8.3) - it skips the
    // Simplifier too, but the store treats any phase list alike; static for CA1861.
    private static readonly WorkflowPhase[] ReviewPhases = [WorkflowPhase.Review, WorkflowPhase.ResolveReview];

    public TaskStateStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wf-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _paths = new TaskPaths(_root, "demo");
        Directory.CreateDirectory(_paths.TaskDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void TryLoad_NoJournal_ReturnsNull() => Assert.Null(_store.TryLoad(_paths));

    [Fact]
    public void SaveDescription_CreatesAJournalWithFivePendingPhases()
    {
        _store.SaveDescription(_paths, "eine Beschreibung");

        var state = _store.TryLoad(_paths);

        Assert.NotNull(state);
        Assert.Equal("eine Beschreibung", state.TaskDescription);
        Assert.Equal(5, state.Phases.Count);
        Assert.All(state.Phases, p => Assert.Equal(PhaseStatus.Pending, p.Status));
        Assert.Equal(
            new[]
            {
                WorkflowPhase.Specification, WorkflowPhase.Simplifier, WorkflowPhase.Review,
                WorkflowPhase.ResolveReview, WorkflowPhase.Implementation,
            },
            state.Phases.Select(p => p.Phase));
    }

    [Fact]
    public void RecordPhase_Completed_StampsCompletedUtc()
    {
        _store.SaveDescription(_paths, "d");
        _store.RecordPhase(_paths, WorkflowPhase.Review, PhaseStatus.Completed);

        var review = _store.TryLoad(_paths)!.Phases.Single(p => p.Phase == WorkflowPhase.Review);

        Assert.Equal(PhaseStatus.Completed, review.Status);
        Assert.NotNull(review.CompletedUtc);
    }

    [Fact]
    public void RecordPhase_Active_ClearsCompletedUtc()
    {
        _store.SaveDescription(_paths, "d");
        _store.RecordPhase(_paths, WorkflowPhase.Review, PhaseStatus.Completed);
        _store.RecordPhase(_paths, WorkflowPhase.Review, PhaseStatus.Active);

        var review = _store.TryLoad(_paths)!.Phases.Single(p => p.Phase == WorkflowPhase.Review);

        Assert.Equal(PhaseStatus.Active, review.Status);
        Assert.Null(review.CompletedUtc);
    }

    [Fact]
    public void RecordPhase_NoJournalYet_CreatesOne()
    {
        _store.RecordPhase(_paths, WorkflowPhase.Specification, PhaseStatus.Active);

        Assert.NotNull(_store.TryLoad(_paths));
    }

    [Fact]
    public void SetDismissed_ThenSaveDescription_ClearsTheFlag()
    {
        _store.SaveDescription(_paths, "d");
        _store.SetDismissed(_paths, dismissed: true);
        Assert.True(_store.TryLoad(_paths)!.Dismissed);

        _store.SaveDescription(_paths, "d");
        Assert.False(_store.TryLoad(_paths)!.Dismissed);
    }

    [Fact]
    public void SetDismissed_NoJournal_IsANoOp()
    {
        _store.SetDismissed(_paths, dismissed: true);

        Assert.False(File.Exists(_paths.StateAbsolute));
    }

    [Fact]
    public void TryLoad_InvalidJson_ReturnsNull()
    {
        File.WriteAllText(_paths.StateAbsolute, "{ this is not json");

        Assert.Null(_store.TryLoad(_paths));
    }

    [Fact]
    public void TryLoad_NewerSchemaVersion_ReturnsNull()
    {
        File.WriteAllText(_paths.StateAbsolute, """{ "version": 99, "phases": [] }""");

        Assert.Null(_store.TryLoad(_paths));
    }

    [Fact]
    public void TryLoad_ShortOrReorderedPhaseArray_NormalisesToCatalogueOrder()
    {
        File.WriteAllText(_paths.StateAbsolute, """
        {
          "version": 1,
          "taskDescription": "d",
          "phases": [
            { "phase": "Review", "status": "Completed", "completedUtc": "2026-09-14T09:00:00Z" },
            { "phase": "Nonsense", "status": "Completed", "completedUtc": null }
          ]
        }
        """);

        var state = _store.TryLoad(_paths);

        Assert.NotNull(state);
        Assert.Equal(5, state.Phases.Count);
        Assert.Equal(WorkflowPhase.Specification, state.Phases[(int)WorkflowPhase.Specification].Phase);
        Assert.Equal(PhaseStatus.Pending, state.Phases[(int)WorkflowPhase.Specification].Status);
        Assert.Equal(PhaseStatus.Pending, state.Phases[(int)WorkflowPhase.Simplifier].Status);
        Assert.Equal(WorkflowPhase.Review, state.Phases[(int)WorkflowPhase.Review].Phase);
        Assert.Equal(PhaseStatus.Completed, state.Phases[(int)WorkflowPhase.Review].Status);
        Assert.Equal(PhaseStatus.Pending, state.Phases[(int)WorkflowPhase.Implementation].Status);
    }

    [Fact]
    public void SaveDescription_LeavesNoTemporaryFileBehind()
    {
        _store.SaveDescription(_paths, "d");

        Assert.Empty(Directory.GetFiles(_paths.TaskDirectory, "*.tmp"));
    }

    [Fact]
    public void TryLoad_UnknownPhaseName_DropsThatEntryAndKeepsTheRest()
    {
        File.WriteAllText(_paths.StateAbsolute, """
        {
          "version": 1,
          "taskDescription": "d",
          "phases": [
            { "phase": "Specification", "status": "Completed", "completedUtc": "2026-09-14T09:00:00Z" },
            { "phase": "Nonsense",      "status": "Completed", "completedUtc": null },
            { "phase": "Review",        "status": "Completed", "completedUtc": "2026-09-14T10:00:00Z" }
          ]
        }
        """);

        var state = _store.TryLoad(_paths);

        // The unknown entry must cost itself, not the journal (SPEC 5.2 / R3 / D19).
        Assert.NotNull(state);
        Assert.Equal("d", state.TaskDescription);
        Assert.Equal(5, state.Phases.Count);
        Assert.Equal(PhaseStatus.Completed, state.Phases[(int)WorkflowPhase.Specification].Status);
        Assert.Equal(PhaseStatus.Pending, state.Phases[(int)WorkflowPhase.Simplifier].Status);
        Assert.Equal(PhaseStatus.Completed, state.Phases[(int)WorkflowPhase.Review].Status);
        Assert.Equal(PhaseStatus.Pending, state.Phases[(int)WorkflowPhase.ResolveReview].Status);
        Assert.Equal(PhaseStatus.Pending, state.Phases[(int)WorkflowPhase.Implementation].Status);
    }

    [Fact]
    public void TryLoad_UnknownStatusName_DefaultsThatPhaseToPending()
    {
        File.WriteAllText(_paths.StateAbsolute, """
        {
          "version": 1,
          "phases": [ { "phase": "Review", "status": "Halfway", "completedUtc": null } ]
        }
        """);

        var state = _store.TryLoad(_paths);

        Assert.NotNull(state);
        Assert.Equal(5, state.Phases.Count);
        Assert.All(state.Phases, p => Assert.Equal(PhaseStatus.Pending, p.Status));
    }

    [Fact]
    public void ReplacePhases_OverwritesTheArrayAndLeavesUpdatedUtcAlone()
    {
        _store.SaveDescription(_paths, "d");
        _store.RecordPhase(_paths, WorkflowPhase.Specification, PhaseStatus.Completed);
        _store.RecordPhase(_paths, WorkflowPhase.Review, PhaseStatus.Completed);

        var before = _store.TryLoad(_paths)!.UpdatedUtc;

        _store.ReplacePhases(
            _paths,
            [.. PhaseCatalog.All.Select(d => new TaskPhaseState(d.Phase, PhaseStatus.Pending, null))]);

        var after = _store.TryLoad(_paths)!;

        Assert.All(after.Phases, p => Assert.Equal(PhaseStatus.Pending, p.Status));
        Assert.All(after.Phases, p => Assert.Null(p.CompletedUtc));
        Assert.Equal("d", after.TaskDescription);

        // A reconciliation records what the disk already said; it is not progress, so it must not
        // slide the task forward inside the 14-day recovery window (SPEC 5.4, 6.3.1).
        Assert.Equal(before, after.UpdatedUtc);
    }

    [Fact]
    public void ReplacePhases_NoJournal_IsANoOp()
    {
        _store.ReplacePhases(
            _paths,
            [.. PhaseCatalog.All.Select(d => new TaskPhaseState(d.Phase, PhaseStatus.Pending, null))]);

        Assert.False(File.Exists(_paths.StateAbsolute));
    }

    [Fact]
    public void SaveDescription_LeavesSubtaskModeDisabledByDefault()
    {
        _store.SaveDescription(_paths, "d");

        var state = _store.TryLoad(_paths);

        // The three subtask fields are additive with safe defaults (design, Configuration and
        // Persistence). A journal that never mentioned them must never read as enabled.
        Assert.NotNull(state);
        Assert.False(state.SubtasksEnabled);
        Assert.Null(state.WorkflowDirectory);
        Assert.False(state.ImplementationCompletedManually);
        Assert.Equal(TaskState.CurrentVersion, state.Version);
    }

    [Fact]
    public void SaveSubtaskSettings_RoundTripsTheModeAndTheDirectory()
    {
        _store.SaveDescription(_paths, "eine Beschreibung");

        _store.SaveSubtaskSettings(_paths, enabled: true, workflowDirectory: @"C:\Workflows\Ablage");

        var state = _store.TryLoad(_paths);

        Assert.NotNull(state);
        Assert.True(state.SubtasksEnabled);
        Assert.Equal(@"C:\Workflows\Ablage", state.WorkflowDirectory);

        // Additive only: the description and the untouched manual marker survive (1.5).
        Assert.Equal("eine Beschreibung", state.TaskDescription);
        Assert.False(state.ImplementationCompletedManually);
    }

    [Fact]
    public void SaveSubtaskSettings_LeavesTheRecordVersionAtOne()
    {
        _store.SaveSubtaskSettings(_paths, enabled: true, workflowDirectory: @"C:\Workflows\Ablage");

        // "all three are additive with safe defaults, so version stays 1 and old records load
        // unchanged" - design, Configuration and Persistence.
        Assert.Equal(1, TaskState.CurrentVersion);
        Assert.Equal(1, _store.TryLoad(_paths)!.Version);
        Assert.Contains("\"version\": 1", File.ReadAllText(_paths.StateAbsolute), StringComparison.Ordinal);
    }

    [Fact]
    public void SaveSubtaskSettings_WritesTheDocumentedCamelCaseNames()
    {
        _store.SaveSubtaskSettings(_paths, enabled: true, workflowDirectory: @"C:\Workflows\Ablage");

        var json = File.ReadAllText(_paths.StateAbsolute);

        Assert.Contains("\"subtasksEnabled\": true", json, StringComparison.Ordinal);
        Assert.Contains("\"workflowDirectory\": \"C:\\\\Workflows\\\\Ablage\"", json, StringComparison.Ordinal);
        Assert.Contains("\"implementationCompletedManually\": false", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveSubtaskSettings_NoJournalYet_CreatesOne()
    {
        _store.SaveSubtaskSettings(_paths, enabled: true, workflowDirectory: @"C:\Workflows\Ablage");

        var state = _store.TryLoad(_paths);

        Assert.NotNull(state);
        Assert.True(state.SubtasksEnabled);
        Assert.Equal(@"C:\Workflows\Ablage", state.WorkflowDirectory);
        Assert.Equal(5, state.Phases.Count);
    }

    [Fact]
    public void SaveSubtaskSettings_TaskDirectoryMissing_DoesNotThrow()
    {
        Directory.Delete(_paths.TaskDirectory, recursive: true);

        // The store's contract is "no method ever throws" (ITaskStateStore). Save recreates the
        // directory, exactly as SaveDescription does.
        _store.SaveSubtaskSettings(_paths, enabled: true, workflowDirectory: @"C:\Workflows\Ablage");

        Assert.True(_store.TryLoad(_paths)!.SubtasksEnabled);
    }

    [Fact]
    public void SaveSubtaskSettings_KeepsTheDismissedFlag()
    {
        _store.SaveDescription(_paths, "d");
        _store.SetDismissed(_paths, dismissed: true);
        Assert.True(_store.TryLoad(_paths)!.Dismissed);

        _store.SaveSubtaskSettings(_paths, enabled: true, workflowDirectory: @"C:\Workflows\Ablage");

        var state = _store.TryLoad(_paths);

        // Unlike SaveDescription, persisting subtask settings is not "the user started this task",
        // so it must not clear the dismissal (task 2.4, design: "preserves Dismissed").
        Assert.NotNull(state);
        Assert.True(state.Dismissed);
        Assert.True(state.SubtasksEnabled);
    }

    [Fact]
    public void SaveSubtaskSettings_PersistsACallerSuppliedNullDirectory()
    {
        _store.SaveSubtaskSettings(_paths, enabled: true, workflowDirectory: @"C:\Workflows\Ablage");

        _store.SaveSubtaskSettings(_paths, enabled: false, workflowDirectory: null);

        var state = _store.TryLoad(_paths);

        Assert.NotNull(state);
        Assert.False(state.SubtasksEnabled);
        Assert.Null(state.WorkflowDirectory);
    }

    [Fact]
    public void SaveSubtaskSettings_KeepsTheRecordedPhases()
    {
        _store.SaveDescription(_paths, "d");
        _store.RecordPhase(_paths, WorkflowPhase.Review, PhaseStatus.Completed);

        _store.SaveSubtaskSettings(_paths, enabled: true, workflowDirectory: @"C:\Workflows\Ablage");

        var state = _store.TryLoad(_paths);

        Assert.NotNull(state);
        Assert.Equal(PhaseStatus.Completed, state.Phases[(int)WorkflowPhase.Review].Status);
        Assert.Equal(PhaseStatus.Pending, state.Phases[(int)WorkflowPhase.Specification].Status);
    }

    [Fact]
    public void SetImplementationCompletedManually_RoundTripsAndKeepsEverythingElse()
    {
        _store.SaveDescription(_paths, "eine Beschreibung");
        _store.SaveSubtaskSettings(_paths, enabled: true, workflowDirectory: @"C:\Workflows\Ablage");
        _store.SetDismissed(_paths, dismissed: true);
        _store.RecordPhase(_paths, WorkflowPhase.Review, PhaseStatus.Completed);

        _store.SetImplementationCompletedManually(_paths, completedManually: true);

        var state = _store.TryLoad(_paths);

        // The explicit manual override recovery reads instead of the ledger (5.4, design issue 6b).
        Assert.NotNull(state);
        Assert.True(state.ImplementationCompletedManually);
        Assert.True(state.SubtasksEnabled);
        Assert.Equal(@"C:\Workflows\Ablage", state.WorkflowDirectory);
        Assert.True(state.Dismissed);
        Assert.Equal("eine Beschreibung", state.TaskDescription);

        // The phase ledger is the other half of "manual completion survives a restart"
        // (requirement 5.4): the marker is only useful if the run it completes is still there
        // after the reload. Without this, the entailment rested on Save being shared with the
        // neighbouring SaveSubtaskSettings_KeepsTheRecordedPhases rather than on this path.
        Assert.Equal(PhaseStatus.Completed, state.Phases[(int)WorkflowPhase.Review].Status);
        Assert.Equal(PhaseStatus.Pending, state.Phases[(int)WorkflowPhase.Specification].Status);
    }

    [Fact]
    public void SetImplementationCompletedManually_False_ClearsTheMarker()
    {
        _store.SaveDescription(_paths, "d");
        _store.SetImplementationCompletedManually(_paths, completedManually: true);

        _store.SetImplementationCompletedManually(_paths, completedManually: false);

        Assert.False(_store.TryLoad(_paths)!.ImplementationCompletedManually);
    }

    [Fact]
    public void SetImplementationCompletedManually_NoJournal_IsANoOp()
    {
        _store.SetImplementationCompletedManually(_paths, completedManually: true);

        Assert.False(File.Exists(_paths.StateAbsolute));
    }

    [Fact]
    public void TryLoad_JournalCarryingSubtaskFields_ReadsAllThree()
    {
        File.WriteAllText(_paths.StateAbsolute, """
        {
          "version": 1,
          "taskDescription": "mit Subtasks",
          "dismissed": false,
          "subtasksEnabled": true,
          "workflowDirectory": "C:\\Workflows\\Ablage",
          "implementationCompletedManually": true,
          "phases": [ { "phase": "Implementation", "status": "Completed", "completedUtc": null } ]
        }
        """);

        var state = _store.TryLoad(_paths);

        Assert.NotNull(state);
        Assert.True(state.SubtasksEnabled);
        Assert.Equal(@"C:\Workflows\Ablage", state.WorkflowDirectory);
        Assert.True(state.ImplementationCompletedManually);
        Assert.Equal("mit Subtasks", state.TaskDescription);
    }

    [Fact]
    public void TryLoad_JournalWrittenBeforeTheSubtaskFieldsExisted_LoadsAsDisabled()
    {
        // Exactly the shape this build wrote before task 2.4: no subtask properties at all.
        File.WriteAllText(_paths.StateAbsolute, """
        {
          "version": 1,
          "taskDescription": "alte Aufgabe",
          "createdUtc": "2026-09-14T08:00:00+00:00",
          "updatedUtc": "2026-09-14T09:30:00+00:00",
          "dismissed": true,
          "phases": [
            { "phase": "Specification", "status": "Completed", "completedUtc": "2026-09-14T09:00:00Z" },
            { "phase": "Review",        "status": "Active",    "completedUtc": null }
          ]
        }
        """);

        var state = _store.TryLoad(_paths);

        Assert.NotNull(state);

        // A malformed fixture would be rescued by the JsonException catch and would be
        // indistinguishable from "loaded but disabled". These four assertions can only hold if the
        // fixture really went through the deserializer, so the defaults below mean something.
        Assert.Equal("alte Aufgabe", state.TaskDescription);
        Assert.True(state.Dismissed);
        Assert.Equal(PhaseStatus.Completed, state.Phases[(int)WorkflowPhase.Specification].Status);
        Assert.Equal(PhaseStatus.Active, state.Phases[(int)WorkflowPhase.Review].Status);

        // E15-E16 / requirement 1.7: old tasks are subtask mode disabled.
        Assert.False(state.SubtasksEnabled);
        Assert.Null(state.WorkflowDirectory);
        Assert.False(state.ImplementationCompletedManually);
    }

    [Fact]
    public void SaveDescription_TaskDirectoryMissing_RecreatesItAndTheJournal()
    {
        Directory.Delete(_paths.TaskDirectory, recursive: true);

        _store.SaveDescription(_paths, "d");

        // Save calls Directory.CreateDirectory, so the contract is "does not throw AND recreates".
        // Asserting Null here would contradict the implementation; the assertion and the
        // implementation are chosen together, up front, not reconciled after a red test.
        Assert.NotNull(_store.TryLoad(_paths));
        Assert.Equal("d", _store.TryLoad(_paths)!.TaskDescription);
    }

    [Fact]
    public void RecordPhase_Skipped_StampsCompletedUtcAndIsWrittenByName()
    {
        // Arrange
        _store.SaveDescription(_paths, "d");

        // Act
        _store.RecordPhase(_paths, WorkflowPhase.Review, PhaseStatus.Skipped);

        // Assert - the skip time is the baseline the next phase's evidence is compared against.
        var review = _store.TryLoad(_paths)!.Phases.Single(p => p.Phase == WorkflowPhase.Review);
        Assert.Equal(PhaseStatus.Skipped, review.Status);
        Assert.NotNull(review.CompletedUtc);
        Assert.Contains("\"Skipped\"", File.ReadAllText(_paths.StateAbsolute), StringComparison.Ordinal);
    }

    [Fact]
    public void TryLoad_HandEditedSkippedStatus_IsReadInAnyCasing()
    {
        // Arrange - the hand-editing form of Workflow_LoadFix spec section 7.5.
        File.WriteAllText(_paths.StateAbsolute, """
        {
          "version": 1,
          "phases": [
            { "phase": "Specification", "status": "Completed", "completedUtc": "2026-10-06T08:00:00Z" },
            { "phase": "Review",        "status": "Skipped",   "completedUtc": null },
            { "phase": "ResolveReview", "status": "skipped",   "completedUtc": null }
          ]
        }
        """);

        // Act
        var state = _store.TryLoad(_paths);

        // Assert
        Assert.NotNull(state);
        Assert.Equal(PhaseStatus.Completed, state.Phases[(int)WorkflowPhase.Specification].Status);
        Assert.Equal(PhaseStatus.Pending, state.Phases[(int)WorkflowPhase.Simplifier].Status);
        Assert.Equal(PhaseStatus.Skipped, state.Phases[(int)WorkflowPhase.Review].Status);
        Assert.Equal(PhaseStatus.Skipped, state.Phases[(int)WorkflowPhase.ResolveReview].Status);
        Assert.Equal(PhaseStatus.Pending, state.Phases[(int)WorkflowPhase.Implementation].Status);
    }

    [Fact]
    public void TryCreate_NoJournal_WritesFivePendingPhasesWithAnEmptyDescription()
    {
        // Act
        var created = _store.TryCreate(_paths);

        // Assert
        Assert.True(created);
        var state = _store.TryLoad(_paths);
        Assert.NotNull(state);
        Assert.Equal(string.Empty, state.TaskDescription);
        Assert.False(state.Dismissed);
        Assert.Equal(5, state.Phases.Count);
        Assert.All(state.Phases, p => Assert.Equal(PhaseStatus.Pending, p.Status));
        Assert.Empty(Directory.GetFiles(_paths.TaskDirectory, "*.tmp"));
    }

    [Fact]
    public void TryCreate_ReadableJournal_ReturnsFalseAndLeavesItUnchanged()
    {
        // Arrange
        _store.SaveDescription(_paths, "vorhanden");
        var before = File.ReadAllBytes(_paths.StateAbsolute);

        // Act
        var created = _store.TryCreate(_paths);

        // Assert
        Assert.False(created);
        Assert.Equal(before, File.ReadAllBytes(_paths.StateAbsolute));
    }

    [Fact]
    public void TryCreate_UnreadableJournal_ReturnsFalseAndNeverOverwritesIt()
    {
        // Arrange - Workflow_LoadFix D3: it may be a newer build's journal or a hand edit in progress.
        File.WriteAllText(_paths.StateAbsolute, "{ this is not json");

        // Act
        var created = _store.TryCreate(_paths);

        // Assert
        Assert.False(created);
        Assert.Equal("{ this is not json", File.ReadAllText(_paths.StateAbsolute));
    }

    [Fact]
    public void TryCreate_TaskDirectoryMissing_ReturnsFalseAndCreatesNothing()
    {
        // Arrange
        var missing = new TaskPaths(_root, "gibt-es-nicht");

        // Act
        var created = _store.TryCreate(missing);

        // Assert
        Assert.False(created);
        Assert.False(Directory.Exists(missing.TaskDirectory));
    }

    [Fact]
    public void TryCreate_TargetBlockedByADirectory_ReturnsFalseAndLeavesNoTemporaryFile()
    {
        // Arrange - the publishing move fails after the temporary file was written (spec F4).
        Directory.CreateDirectory(_paths.StateAbsolute);

        // Act
        var created = _store.TryCreate(_paths);

        // Assert
        Assert.False(created);
        Assert.True(Directory.Exists(_paths.StateAbsolute));
        Assert.Empty(Directory.GetFiles(_paths.TaskDirectory, "*.tmp"));
    }

    [Fact]
    public void TryCreate_SecondStoreAfterTheFirst_ReturnsFalseAndKeepsTheFirstJournalsBytes()
    {
        // Arrange - two instances, so nothing in-process can serialise them (spec F20).
        var first = new TaskStateStore();
        var second = new TaskStateStore();
        Assert.True(first.TryCreate(_paths));
        var before = File.ReadAllBytes(_paths.StateAbsolute);

        // Act
        var created = second.TryCreate(_paths);

        // Assert
        Assert.False(created);
        Assert.Equal(before, File.ReadAllBytes(_paths.StateAbsolute));
    }

    [Fact]
    public void TryCreate_ManyStoresAtOnce_ExactlyOneSucceeds()
    {
        // Arrange - every creator is released by the same barrier, so the publishing moves race
        // (Workflow_LoadFix AC-1): the file system's refusal must leave exactly one winner.
        const int creators = 8;
        var results = new bool[creators];
        using var start = new Barrier(creators);
        var threads = Enumerable.Range(0, creators)
            .Select(i => new Thread(() =>
            {
                var store = new TaskStateStore();
                start.SignalAndWait();
                results[i] = store.TryCreate(_paths);
            }))
            .ToList();

        // Act
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        // Assert
        Assert.Single(results, created => created);
        Assert.NotNull(_store.TryLoad(_paths));
        Assert.Empty(Directory.GetFiles(_paths.TaskDirectory, "*.tmp"));
    }

    [Fact]
    public void TrySkipPhases_BothReviewPhasesPending_SkipsBothWithOneStamp()
    {
        // Arrange
        _store.SaveDescription(_paths, "d");
        _store.RecordPhase(_paths, WorkflowPhase.Specification, PhaseStatus.Completed);
        var before = _store.TryLoad(_paths)!;

        // Act
        var result = _store.TrySkipPhases(_paths, ReviewPhases);

        // Assert
        Assert.NotNull(result);
        var onDisk = _store.TryLoad(_paths);
        Assert.NotNull(onDisk);
        Assert.Equal(PhaseStatus.Skipped, onDisk.Phases[(int)WorkflowPhase.Review].Status);
        Assert.Equal(PhaseStatus.Skipped, onDisk.Phases[(int)WorkflowPhase.ResolveReview].Status);
        Assert.NotNull(onDisk.Phases[(int)WorkflowPhase.Review].CompletedUtc);
        Assert.NotNull(onDisk.Phases[(int)WorkflowPhase.ResolveReview].CompletedUtc);
        Assert.Equal(onDisk.Phases[(int)WorkflowPhase.Review].CompletedUtc, onDisk.Phases[(int)WorkflowPhase.ResolveReview].CompletedUtc);
        Assert.Equal(before.Phases[(int)WorkflowPhase.Specification], onDisk.Phases[(int)WorkflowPhase.Specification]);
        Assert.Equal(before.Phases[(int)WorkflowPhase.Simplifier], onDisk.Phases[(int)WorkflowPhase.Simplifier]);
        Assert.Equal(before.Phases[(int)WorkflowPhase.Implementation], onDisk.Phases[(int)WorkflowPhase.Implementation]);
    }

    [Fact]
    public void TrySkipPhases_ReviewAlreadyCompleted_KeepsItCompletedAndSkipsOnlyResolveReview()
    {
        // Arrange - another writer recorded the Review before this read-modify-write (spec F24).
        _store.SaveDescription(_paths, "d");
        _store.RecordPhase(_paths, WorkflowPhase.Specification, PhaseStatus.Completed);
        new TaskStateStore().RecordPhase(_paths, WorkflowPhase.Review, PhaseStatus.Completed);
        var reviewBefore = _store.TryLoad(_paths)!.Phases[(int)WorkflowPhase.Review];

        // Act
        var result = _store.TrySkipPhases(_paths, ReviewPhases);

        // Assert
        Assert.NotNull(result);
        var onDisk = _store.TryLoad(_paths);
        Assert.NotNull(onDisk);
        Assert.Equal(reviewBefore, onDisk.Phases[(int)WorkflowPhase.Review]);
        Assert.Equal(PhaseStatus.Completed, onDisk.Phases[(int)WorkflowPhase.Review].Status);
        Assert.Equal(PhaseStatus.Skipped, onDisk.Phases[(int)WorkflowPhase.ResolveReview].Status);
    }

    [Fact]
    public void TrySkipPhases_AlreadySkipped_WritesNothingAndReturnsTheJournal()
    {
        // Arrange
        _store.SaveDescription(_paths, "d");
        _store.RecordPhase(_paths, WorkflowPhase.Review, PhaseStatus.Skipped);
        _store.RecordPhase(_paths, WorkflowPhase.ResolveReview, PhaseStatus.Skipped);
        var before = File.ReadAllBytes(_paths.StateAbsolute);

        // Act
        var result = _store.TrySkipPhases(_paths, ReviewPhases);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(PhaseStatus.Skipped, result.Phases[(int)WorkflowPhase.Review].Status);
        Assert.Equal(PhaseStatus.Skipped, result.Phases[(int)WorkflowPhase.ResolveReview].Status);
        Assert.Equal(before, File.ReadAllBytes(_paths.StateAbsolute));
    }

    [Fact]
    public void TrySkipPhases_NoJournal_ReturnsNullAndCreatesNone()
    {
        // Act
        var result = _store.TrySkipPhases(_paths, ReviewPhases);

        // Assert
        Assert.Null(result);
        Assert.False(File.Exists(_paths.StateAbsolute));
    }

    [Fact]
    public void TrySkipPhases_UnreadableJournal_ReturnsNullAndLeavesItUnchanged()
    {
        // Arrange
        File.WriteAllText(_paths.StateAbsolute, "{ this is not json");
        var before = File.ReadAllBytes(_paths.StateAbsolute);

        // Act
        var result = _store.TrySkipPhases(_paths, ReviewPhases);

        // Assert
        Assert.Null(result);
        Assert.Equal(before, File.ReadAllBytes(_paths.StateAbsolute));
    }

    [Fact]
    public void TrySkipPhases_WriteFails_ReturnsNullAndChangesNothing()
    {
        // Arrange - a directory on the save's temporary path makes the swallowed write fail (spec F22):
        // only the read-back can tell.
        _store.SaveDescription(_paths, "d");
        _store.RecordPhase(_paths, WorkflowPhase.Specification, PhaseStatus.Completed);
        var before = File.ReadAllBytes(_paths.StateAbsolute);
        Directory.CreateDirectory(_paths.StateAbsolute + ".tmp");

        // Act
        var result = _store.TrySkipPhases(_paths, ReviewPhases);

        // Assert
        Assert.Null(result);
        Assert.Equal(before, File.ReadAllBytes(_paths.StateAbsolute));
    }

    [Fact]
    public void TryLoad_LegacyFourPhaseJournal_LoadsFiveEntriesWithTheSimplifierPending()
    {
        // Arrange - exactly what a build without the Simplifier phase wrote: four entries, all done.
        File.WriteAllText(_paths.StateAbsolute, """
        {
          "version": 1,
          "taskDescription": "alte Aufgabe",
          "phases": [
            { "phase": "Specification",  "status": "Completed", "completedUtc": "2026-09-14T09:00:00Z" },
            { "phase": "Review",         "status": "Completed", "completedUtc": "2026-09-14T10:00:00Z" },
            { "phase": "ResolveReview",  "status": "Completed", "completedUtc": "2026-09-14T11:00:00Z" },
            { "phase": "Implementation", "status": "Completed", "completedUtc": "2026-09-14T12:00:00Z" }
          ]
        }
        """);

        // Act
        var state = _store.TryLoad(_paths);

        // Assert - the store only normalises; migrating the gap is reconciliation's job.
        Assert.NotNull(state);
        Assert.Equal(
            new[]
            {
                WorkflowPhase.Specification, WorkflowPhase.Simplifier, WorkflowPhase.Review,
                WorkflowPhase.ResolveReview, WorkflowPhase.Implementation,
            },
            state.Phases.Select(p => p.Phase));
        Assert.Equal(PhaseStatus.Pending, state.Phases[(int)WorkflowPhase.Simplifier].Status);
        Assert.Null(state.Phases[(int)WorkflowPhase.Simplifier].CompletedUtc);
        Assert.Equal(PhaseStatus.Completed, state.Phases[(int)WorkflowPhase.Specification].Status);
        Assert.Equal(PhaseStatus.Completed, state.Phases[(int)WorkflowPhase.Review].Status);
        Assert.Equal(PhaseStatus.Completed, state.Phases[(int)WorkflowPhase.ResolveReview].Status);
        Assert.Equal(PhaseStatus.Completed, state.Phases[(int)WorkflowPhase.Implementation].Status);
        Assert.Equal(
            new DateTimeOffset(2026, 9, 14, 10, 0, 0, TimeSpan.Zero),
            state.Phases[(int)WorkflowPhase.Review].CompletedUtc);
    }
}
