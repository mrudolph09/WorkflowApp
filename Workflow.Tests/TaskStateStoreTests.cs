using System.IO;
using Workflow.Models;
using Workflow.Services;

namespace Workflow.Tests;

public sealed class TaskStateStoreTests : IDisposable
{
    private readonly string _root;
    private readonly TaskPaths _paths;
    private readonly TaskStateStore _store = new();

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
    public void SaveDescription_CreatesAJournalWithFourPendingPhases()
    {
        _store.SaveDescription(_paths, "eine Beschreibung");

        var state = _store.TryLoad(_paths);

        Assert.NotNull(state);
        Assert.Equal("eine Beschreibung", state.TaskDescription);
        Assert.Equal(4, state.Phases.Count);
        Assert.All(state.Phases, p => Assert.Equal(PhaseStatus.Pending, p.Status));
        Assert.Equal(
            new[]
            {
                WorkflowPhase.Specification, WorkflowPhase.Review,
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
        Assert.Equal(4, state.Phases.Count);
        Assert.Equal(WorkflowPhase.Specification, state.Phases[0].Phase);
        Assert.Equal(PhaseStatus.Pending, state.Phases[0].Status);
        Assert.Equal(PhaseStatus.Completed, state.Phases[1].Status);
        Assert.Equal(PhaseStatus.Pending, state.Phases[3].Status);
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
        Assert.Equal(4, state.Phases.Count);
        Assert.Equal(PhaseStatus.Completed, state.Phases[0].Status);
        Assert.Equal(PhaseStatus.Completed, state.Phases[1].Status);
        Assert.Equal(PhaseStatus.Pending, state.Phases[2].Status);
        Assert.Equal(PhaseStatus.Pending, state.Phases[3].Status);
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
        Assert.Equal(4, state.Phases.Count);
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
        Assert.Equal(4, state.Phases.Count);
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
        Assert.Equal(PhaseStatus.Completed, state.Phases[1].Status);
        Assert.Equal(PhaseStatus.Pending, state.Phases[0].Status);
    }

    [Fact]
    public void SetImplementationCompletedManually_RoundTripsAndKeepsEverythingElse()
    {
        _store.SaveDescription(_paths, "eine Beschreibung");
        _store.SaveSubtaskSettings(_paths, enabled: true, workflowDirectory: @"C:\Workflows\Ablage");
        _store.SetDismissed(_paths, dismissed: true);

        _store.SetImplementationCompletedManually(_paths, completedManually: true);

        var state = _store.TryLoad(_paths);

        // The explicit manual override recovery reads instead of the ledger (5.4, design issue 6b).
        Assert.NotNull(state);
        Assert.True(state.ImplementationCompletedManually);
        Assert.True(state.SubtasksEnabled);
        Assert.Equal(@"C:\Workflows\Ablage", state.WorkflowDirectory);
        Assert.True(state.Dismissed);
        Assert.Equal("eine Beschreibung", state.TaskDescription);
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
        Assert.Equal(PhaseStatus.Completed, state.Phases[0].Status);
        Assert.Equal(PhaseStatus.Active, state.Phases[1].Status);

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
}
