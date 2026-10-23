using System.IO;
using Workflow.Models;
using Workflow.Services;

namespace Workflow.Tests;

public sealed class TaskRecoveryScannerTests : IDisposable
{
    private readonly string _root;
    private readonly SettingsService _settings;
    private readonly TaskStateStore _store = new();

    // The three phases "Implementierung beginnen" skips (Workflow_LoadFix spec 8.3); static for CA1861.
    private static readonly WorkflowPhase[] SkippedPhases =
        [WorkflowPhase.Simplifier, WorkflowPhase.Review, WorkflowPhase.ResolveReview];

    public TaskRecoveryScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wf-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _settings = new SettingsService(Path.Combine(_root, "settings.json"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string NewWorkspace(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        _settings.AddRecentDirectory(path);
        return path;
    }

    private TaskPaths SeedTask(
        string workspace,
        string taskName,
        params (WorkflowPhase Phase, PhaseStatus Status)[] statuses)
    {
        var paths = new TaskPaths(workspace, taskName);
        Directory.CreateDirectory(paths.TaskDirectory);
        _store.SaveDescription(paths, $"Beschreibung von {taskName}");

        foreach (var (phase, status) in statuses)
        {
            _store.RecordPhase(paths, phase, status);

            if (status == PhaseStatus.Completed)
            {
                WriteArtefactsFor(paths, phase);
            }
        }

        return paths;
    }

    private static void WriteArtefactsFor(TaskPaths paths, WorkflowPhase phase)
    {
        switch (phase)
        {
            case WorkflowPhase.Specification:
                File.WriteAllText(paths.SpecAbsolute, "spec");
                File.WriteAllText(paths.PlanAbsolute, "plan");
                break;
            case WorkflowPhase.Simplifier:
                File.WriteAllText(paths.SimplifiedAbsolute, "simplified");
                break;
            case WorkflowPhase.Review:
                File.WriteAllText(paths.ReviewAbsolute, "review");
                break;
            case WorkflowPhase.Implementation:
                File.WriteAllText(paths.DoneAbsolute, "done");
                break;
            default:
                break;
        }
    }

    /// <summary>A fixed instant, <paramref name="minute"/> minutes into a fixed day.</summary>
    private static DateTimeOffset Minute(int minute) =>
        new DateTimeOffset(2026, 10, 1, 15, 0, 0, TimeSpan.Zero).AddMinutes(minute);

    /// <summary>Writes an artefact and pins its last-write time, so ordering never rides on the clock.</summary>
    private static void WriteAt(string path, string content, DateTimeOffset writtenUtc)
    {
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, writtenUtc.UtcDateTime);
    }

    private TaskRecoveryScanner Create() => new(
        _settings,
        _store,
        maxAge: TimeSpan.FromDays(14),
        maxTasks: 5,
        maxSubdirectoriesPerRoot: 2000,
        scanTimeout: TimeSpan.FromSeconds(5));

    [Fact]
    public async Task ScanAsync_UnfinishedTask_IsOffered()
    {
        var workspace = NewWorkspace("ws");
        SeedTask(workspace, "alpha", (WorkflowPhase.Specification, PhaseStatus.Completed));

        var found = await Create().ScanAsync(CancellationToken.None);

        var task = Assert.Single(found);
        Assert.Equal("alpha", task.Paths.TaskName);
        Assert.Equal(WorkflowPhase.Simplifier, task.ResumePhase);
        Assert.Equal("Beschreibung von alpha", task.State.TaskDescription);
    }

    [Fact]
    public async Task ScanAsync_FolderWithoutAJournal_IsIgnored()
    {
        // Arrange
        var workspace = NewWorkspace("ws");
        Directory.CreateDirectory(Path.Combine(workspace, "not-a-task"));

        // Act
        var found = await Create().ScanAsync(CancellationToken.None);

        // Assert - only 'Task laden' creates a journal, never the startup scan (Workflow_LoadFix 5.2).
        Assert.Empty(found);
        Assert.False(File.Exists(new TaskPaths(workspace, "not-a-task").StateAbsolute));
    }

    [Fact]
    public async Task ScanAsync_AllPhasesCompleted_IsNotOffered()
    {
        var workspace = NewWorkspace("ws");
        SeedTask(
            workspace,
            "done",
            (WorkflowPhase.Specification, PhaseStatus.Completed),
            (WorkflowPhase.Simplifier, PhaseStatus.Completed),
            (WorkflowPhase.Review, PhaseStatus.Completed),
            (WorkflowPhase.ResolveReview, PhaseStatus.Completed),
            (WorkflowPhase.Implementation, PhaseStatus.Completed));

        Assert.Empty(await Create().ScanAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ScanAsync_DismissedTask_IsNotOffered()
    {
        var workspace = NewWorkspace("ws");
        var paths = SeedTask(workspace, "alpha");
        _store.SetDismissed(paths, dismissed: true);

        Assert.Empty(await Create().ScanAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ScanAsync_JournalOlderThanTheWindow_IsNotOffered()
    {
        var workspace = NewWorkspace("ws");
        SeedTask(workspace, "alpha");

        var scanner = new TaskRecoveryScanner(
            _settings, _store, TimeSpan.Zero, 5, 2000, TimeSpan.FromSeconds(5));

        await Task.Delay(20);
        Assert.Empty(await scanner.ScanAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ScanAsync_MoreThanTheCap_ReturnsTheMostRecentlyUpdated()
    {
        var workspace = NewWorkspace("ws");
        for (var i = 0; i < 7; i++)
        {
            SeedTask(workspace, $"task{i}");
            await Task.Delay(15);
        }

        var found = await new TaskRecoveryScanner(
            _settings, _store, TimeSpan.FromDays(14), 3, 2000, TimeSpan.FromSeconds(5))
            .ScanAsync(CancellationToken.None);

        Assert.Equal(3, found.Count);
        Assert.Equal("task6", found[0].Paths.TaskName);
        Assert.Equal("task4", found[2].Paths.TaskName);
    }

    [Fact]
    public async Task ScanAsync_MissingRecentDirectory_DoesNotThrow()
    {
        var workspace = NewWorkspace("gone");
        Directory.Delete(workspace, recursive: true);

        Assert.Empty(await Create().ScanAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ScanAsync_CompletedPhaseWhoseArtefactVanished_IsDemotedWithEverythingAfterIt()
    {
        var workspace = NewWorkspace("ws");
        var paths = SeedTask(
            workspace,
            "alpha",
            (WorkflowPhase.Specification, PhaseStatus.Completed),
            (WorkflowPhase.Simplifier, PhaseStatus.Completed),
            (WorkflowPhase.Review, PhaseStatus.Completed),
            (WorkflowPhase.ResolveReview, PhaseStatus.Completed));

        File.Delete(paths.PlanAbsolute);

        var task = Assert.Single(await Create().ScanAsync(CancellationToken.None));

        Assert.Equal(WorkflowPhase.Specification, task.ResumePhase);
        Assert.All(task.State.Phases, p => Assert.NotEqual(PhaseStatus.Completed, p.Status));
    }

    [Fact]
    public async Task ScanAsync_Demotion_IsPersistedWithoutTouchingUpdatedUtc()
    {
        var workspace = NewWorkspace("ws");
        var paths = SeedTask(workspace, "alpha", (WorkflowPhase.Specification, PhaseStatus.Completed));

        File.Delete(paths.PlanAbsolute);
        var before = _store.TryLoad(paths)!.UpdatedUtc;

        await Create().ScanAsync(CancellationToken.None);

        var onDisk = _store.TryLoad(paths)!;

        Assert.All(onDisk.Phases, p => Assert.Equal(PhaseStatus.Pending, p.Status));

        // A reconciliation records what the disk already said. Stamping UpdatedUtc would keep the
        // task inside the 14-day window purely because a file was deleted (SPEC 5.4, 6.3.1).
        Assert.Equal(before, onDisk.UpdatedUtc);
    }

    [Fact]
    public async Task ScanAsync_ADemotedTailIsNotResurrectedByASecondCrash()
    {
        var workspace = NewWorkspace("ws");
        var paths = SeedTask(
            workspace,
            "alpha",
            (WorkflowPhase.Specification, PhaseStatus.Completed),
            (WorkflowPhase.Simplifier, PhaseStatus.Completed),
            (WorkflowPhase.Review, PhaseStatus.Completed),
            (WorkflowPhase.ResolveReview, PhaseStatus.Completed));

        // The plan artefact disappears, so the specification - and therefore every later phase - is demoted.
        File.Delete(paths.PlanAbsolute);

        var first = Assert.Single(await Create().ScanAsync(CancellationToken.None));
        Assert.Equal(WorkflowPhase.Specification, first.ResumePhase);

        // The resumed run finishes the specification, then the app dies again. RecordPhase touches
        // ONLY that phase, so everything after it must already have been cleared on disk by the scan.
        await File.WriteAllTextAsync(paths.PlanAbsolute, "plan");
        _store.RecordPhase(paths, WorkflowPhase.Specification, PhaseStatus.Completed);

        var second = Assert.Single(await Create().ScanAsync(CancellationToken.None));

        // The simplifier report and T-review.md are still on disk, but both predate the new
        // specification, so neither promotes nor skips anything. Had the first scan reconciled in
        // memory only, the journal would still claim the simplifier and both review phases are
        // Completed and this would read Implementation - silently skipping three phases
        // (SPEC 6.3.1, R13, D17).
        Assert.Equal(WorkflowPhase.Simplifier, second.ResumePhase);
    }

    /// <summary>
    /// The application died after the CLI wrote spec and plan but before the orchestrator recorded
    /// phase 1. Recovery resumes behind the artefacts, and the promotion is written back so the
    /// run's own RecordPhase calls start from a journal that already agrees with the disk.
    /// </summary>
    [Fact]
    public async Task ScanAsync_SpecificationActiveButArtefactsPresent_ResumesAtSimplifierAndPersistsThePromotion()
    {
        // Arrange
        var workspace = NewWorkspace("ws");
        var paths = SeedTask(workspace, "alpha", (WorkflowPhase.Specification, PhaseStatus.Active));
        WriteAt(paths.SpecAbsolute, "spec", Minute(10));
        WriteAt(paths.PlanAbsolute, "plan", Minute(11));
        var before = _store.TryLoad(paths)!.UpdatedUtc;

        // Act
        var found = await Create().ScanAsync(CancellationToken.None);

        // Assert
        var task = Assert.Single(found);
        Assert.Equal(WorkflowPhase.Simplifier, task.ResumePhase);

        var onDisk = _store.TryLoad(paths)!;
        Assert.Equal(PhaseStatus.Completed, onDisk.Phases[(int)WorkflowPhase.Specification].Status);
        Assert.Equal(Minute(11), onDisk.Phases[(int)WorkflowPhase.Specification].CompletedUtc);
        Assert.Equal(PhaseStatus.Pending, onDisk.Phases[(int)WorkflowPhase.Simplifier].Status);
        Assert.Equal(PhaseStatus.Pending, onDisk.Phases[(int)WorkflowPhase.Review].Status);

        // Like a demotion, a promotion records what the disk already said; it must not keep the
        // task inside the 14-day window on its own.
        Assert.Equal(before, onDisk.UpdatedUtc);
    }

    [Fact]
    public async Task ScanAsync_EveryPhaseSatisfiedOnDisk_IsNotOffered()
    {
        // Arrange
        var workspace = NewWorkspace("ws");
        var paths = SeedTask(workspace, "alpha");
        _store.ReplacePhases(
            paths,
            [
                new TaskPhaseState(WorkflowPhase.Specification, PhaseStatus.Completed, Minute(12)),
                new TaskPhaseState(WorkflowPhase.Simplifier, PhaseStatus.Completed, Minute(21)),
                new TaskPhaseState(WorkflowPhase.Review, PhaseStatus.Completed, Minute(31)),
                new TaskPhaseState(WorkflowPhase.ResolveReview, PhaseStatus.Completed, Minute(45)),
                new TaskPhaseState(WorkflowPhase.Implementation, PhaseStatus.Active, null),
            ]);
        WriteAt(paths.SimplifiedAbsolute, "simplified", Minute(20));
        WriteAt(paths.ReviewAbsolute, "review", Minute(30));
        WriteAt(paths.SpecAbsolute, "spec", Minute(40));
        WriteAt(paths.PlanAbsolute, "plan", Minute(41));
        WriteAt(paths.DoneAbsolute, "done", Minute(90));

        // Act
        var found = await Create().ScanAsync(CancellationToken.None);

        // Assert
        Assert.Empty(found);
        Assert.Equal(PhaseStatus.Completed, _store.TryLoad(paths)!.Phases[(int)WorkflowPhase.Implementation].Status);
    }

    [Fact]
    public async Task ScanAsync_BudgetAlreadyExpired_ReturnsAListWithoutThrowing()
    {
        var workspace = NewWorkspace("ws");
        SeedTask(workspace, "alpha");

        var scanner = new TaskRecoveryScanner(
            _settings, _store, TimeSpan.FromDays(14), 5, 2000, TimeSpan.Zero);

        // The contract is a partial list - never an exception, never a cancelled task.
        Assert.NotNull(await scanner.ScanAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ScanAsync_TokenAlreadyCancelled_ReturnsAListWithoutThrowing()
    {
        var workspace = NewWorkspace("ws");
        SeedTask(workspace, "alpha");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // This is why the worker is started with CancellationToken.None: Task.Run would otherwise
        // hand back a cancelled task instead of the promised partial list.
        Assert.NotNull(await Create().ScanAsync(cts.Token));
    }

    [Fact]
    public async Task ScanAsync_MruMutatedWhileTheScanRuns_DoesNotFault()
    {
        var workspace = NewWorkspace("ws");
        for (var i = 0; i < 40; i++)
        {
            SeedTask(workspace, $"task{i}");
        }

        var scan = Create().ScanAsync(CancellationToken.None);

        // Exactly what the already-visible blank tab does when the user picks a directory while
        // the startup scan is still running.
        for (var i = 0; i < 40; i++)
        {
            _settings.AddRecentDirectory(Path.Combine(_root, $"extra{i}"));
        }

        Assert.NotNull(await scan);
    }

    [Fact]
    public async Task ScanAsync_ResolveReviewCompleted_IsNeverDemotedByDiskEvidence()
    {
        var workspace = NewWorkspace("ws");
        SeedTask(
            workspace,
            "alpha",
            (WorkflowPhase.Specification, PhaseStatus.Completed),
            (WorkflowPhase.Simplifier, PhaseStatus.Completed),
            (WorkflowPhase.Review, PhaseStatus.Completed),
            (WorkflowPhase.ResolveReview, PhaseStatus.Completed));

        var task = Assert.Single(await Create().ScanAsync(CancellationToken.None));

        Assert.Equal(WorkflowPhase.Implementation, task.ResumePhase);
    }

    // --- Load: one folder picked with 'Task laden' (Workflow_LOAD_AND_PAUSE spec 5.4) ------------

    [Fact]
    public void Load_UnfinishedTask_ReturnsItWithItsFirstIncompletePhase()
    {
        // Arrange
        var workspace = NewWorkspace("ws");
        var paths = SeedTask(workspace, "alpha", (WorkflowPhase.Specification, PhaseStatus.Completed));

        // Act
        var task = Create().Load(paths);

        // Assert
        Assert.NotNull(task);
        Assert.False(task.JournalCreated);
        Assert.Equal("alpha", task.Paths.TaskName);
        Assert.Equal(WorkflowPhase.Simplifier, task.ResumePhase);
        Assert.Equal("Beschreibung von alpha", task.State.TaskDescription);
    }

    [Fact]
    public void Load_FinishedTask_IsReturnedWithoutAResumePhase()
    {
        // Arrange - the startup scan drops exactly this task (ScanAsync_AllPhasesCompleted_IsNotOffered).
        var workspace = NewWorkspace("ws");
        var paths = SeedTask(
            workspace,
            "done",
            (WorkflowPhase.Specification, PhaseStatus.Completed),
            (WorkflowPhase.Simplifier, PhaseStatus.Completed),
            (WorkflowPhase.Review, PhaseStatus.Completed),
            (WorkflowPhase.ResolveReview, PhaseStatus.Completed),
            (WorkflowPhase.Implementation, PhaseStatus.Completed));

        // Act
        var task = Create().Load(paths);

        // Assert
        Assert.NotNull(task);
        Assert.Null(task.ResumePhase);
        Assert.All(task.State.Phases, p => Assert.Equal(PhaseStatus.Completed, p.Status));
    }

    [Fact]
    public void Load_DismissedTask_IsNoLongerDismissedOnDisk()
    {
        // Arrange
        var workspace = NewWorkspace("ws");
        var paths = SeedTask(workspace, "alpha");
        _store.SetDismissed(paths, dismissed: true);

        // Act
        var task = Create().Load(paths);

        // Assert
        Assert.NotNull(task);
        var onDisk = _store.TryLoad(paths);
        Assert.NotNull(onDisk);
        Assert.False(onDisk.Dismissed);
    }

    [Fact]
    public async Task Load_JournalOlderThanTheWindow_IsStillReturned()
    {
        // Arrange - a zero window, exactly as ScanAsync_JournalOlderThanTheWindow_IsNotOffered builds it.
        var workspace = NewWorkspace("ws");
        var paths = SeedTask(workspace, "alpha");
        var scanner = new TaskRecoveryScanner(
            _settings, _store, TimeSpan.Zero, 5, 2000, TimeSpan.FromSeconds(5));
        await Task.Delay(20);

        // Act
        var task = scanner.Load(paths);

        // Assert - positive control first: the scan itself does drop it.
        Assert.Empty(await scanner.ScanAsync(CancellationToken.None));
        Assert.NotNull(task);
    }

    [Fact]
    public void Load_DirectoryNotInTheMru_IsStillReturned()
    {
        // Arrange - deliberately NOT NewWorkspace, which would add the directory to the MRU.
        var workspace = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(workspace);
        var paths = SeedTask(workspace, "alpha");

        // Act
        var task = Create().Load(paths);

        // Assert
        Assert.DoesNotContain(workspace, _settings.Settings.RecentDirectories);
        Assert.NotNull(task);
    }

    [Fact]
    public void Load_FolderWithoutAJournal_CreatesOneAndResumesAtSpecification()
    {
        // Arrange - Workflow_LoadFix spec 5.2, superseding Workflow_LOAD_AND_PAUSE decision D1.
        var paths = new TaskPaths(NewWorkspace("ws"), "fresh");
        Directory.CreateDirectory(paths.TaskDirectory);

        // Act
        var task = Create().Load(paths);

        // Assert
        Assert.NotNull(task);
        Assert.True(task.JournalCreated);
        Assert.Equal(WorkflowPhase.Specification, task.ResumePhase);
        Assert.Equal(string.Empty, task.State.TaskDescription);
        var onDisk = _store.TryLoad(paths);
        Assert.NotNull(onDisk);
        Assert.All(onDisk.Phases, p => Assert.Equal(PhaseStatus.Pending, p.Status));
    }

    [Fact]
    public void Load_FolderWithSpecAndPlanButNoJournal_CreatesOneWithTheSpecificationCompleted()
    {
        // Arrange - the user's case: a spec and a plan written outside this application.
        var paths = new TaskPaths(NewWorkspace("ws"), "planned");
        Directory.CreateDirectory(paths.TaskDirectory);
        File.WriteAllText(Path.Combine(paths.TaskDirectory, "task_plan.md"), "task plan");
        WriteAt(paths.SpecAbsolute, "spec", Minute(1));
        WriteAt(paths.PlanAbsolute, "plan", Minute(2));

        // Act
        var task = Create().Load(paths);

        // Assert
        Assert.NotNull(task);
        Assert.True(task.JournalCreated);
        Assert.Equal(WorkflowPhase.Simplifier, task.ResumePhase);
        var onDisk = _store.TryLoad(paths);
        Assert.NotNull(onDisk);
        Assert.Equal(PhaseStatus.Completed, onDisk.Phases[(int)WorkflowPhase.Specification].Status);
        Assert.Equal(Minute(2), onDisk.Phases[(int)WorkflowPhase.Specification].CompletedUtc);
        Assert.Equal(PhaseStatus.Pending, onDisk.Phases[(int)WorkflowPhase.Simplifier].Status);
        Assert.Equal(PhaseStatus.Pending, onDisk.Phases[(int)WorkflowPhase.Review].Status);
    }

    [Fact]
    public void Load_AfterAZeroByteSpecWasReplacedByAdoption_CompletesTheSpecification()
    {
        // Arrange - Workflow_LoadFix F25: a 0-byte spec is absent evidence until a chosen file
        // replaces it; the adopted file keeps its write time, which dates phase 1.
        var paths = new TaskPaths(NewWorkspace("ws"), "placeholder");
        Directory.CreateDirectory(paths.TaskDirectory);
        File.WriteAllBytes(paths.SpecAbsolute, []);
        WriteAt(paths.PlanAbsolute, "plan", Minute(2));
        var draft = Path.Combine(paths.TaskDirectory, "entwurf-design.md");
        WriteAt(draft, "spec", Minute(3));
        new TaskFolderService().AdoptArtefact(draft, paths.SpecAbsolute);

        // Act
        var task = Create().Load(paths);

        // Assert
        Assert.NotNull(task);
        Assert.Equal(WorkflowPhase.Simplifier, task.ResumePhase);
        Assert.Equal(PhaseStatus.Completed, task.State.Phases[(int)WorkflowPhase.Specification].Status);
        Assert.Equal(Minute(3), task.State.Phases[(int)WorkflowPhase.Specification].CompletedUtc);
    }

    [Fact]
    public void Load_UnreadableJournal_ReturnsNullAndChangesNothing()
    {
        // Arrange - Workflow_LoadFix D3: a journal is never overwritten, readable or not.
        var paths = new TaskPaths(NewWorkspace("ws"), "broken");
        Directory.CreateDirectory(paths.TaskDirectory);
        File.WriteAllText(paths.StateAbsolute, "{ this is not json");
        File.WriteAllText(paths.SpecAbsolute, "spec");
        File.WriteAllText(paths.PlanAbsolute, "plan");
        var before = SnapshotFolder(paths.TaskDirectory);

        // Act
        var task = Create().Load(paths);

        // Assert
        Assert.Null(task);
        Assert.Equal(before, SnapshotFolder(paths.TaskDirectory));
    }

    [Fact]
    public void Load_FolderThatNoLongerExists_ReturnsNullAndCreatesNothing()
    {
        // Arrange - picked, then deleted before the load.
        var paths = new TaskPaths(NewWorkspace("ws"), "vanished");

        // Act
        var task = Create().Load(paths);

        // Assert
        Assert.Null(task);
        Assert.False(Directory.Exists(paths.TaskDirectory));
    }

    [Fact]
    public void Load_AnotherCreatorWinsTheRace_LoadsThatJournalAndReportsNotCreated()
    {
        // Arrange - Workflow_LoadFix spec 5.2 step 2: TryCreate loses to a journal another process
        // publishes in the same moment, and Load reads that journal instead of giving up.
        var paths = new TaskPaths(NewWorkspace("ws"), "raced");
        Directory.CreateDirectory(paths.TaskDirectory);
        var scanner = new TaskRecoveryScanner(
            _settings,
            new RacingStateStore(_store),
            maxAge: TimeSpan.FromDays(14),
            maxTasks: 5,
            maxSubdirectoriesPerRoot: 2000,
            scanTimeout: TimeSpan.FromSeconds(5));

        // Act
        var task = scanner.Load(paths);

        // Assert
        Assert.NotNull(task);
        Assert.False(task.JournalCreated);
        Assert.Equal("fremd", task.State.TaskDescription);
    }

    [Fact]
    public void Load_ReviewPhasesSkipped_ResumesAtImplementation()
    {
        // Arrange - what 'Implementierung beginnen' leaves behind before the implementation has run.
        var workspace = NewWorkspace("ws");
        var paths = SeedTask(workspace, "alpha", (WorkflowPhase.Specification, PhaseStatus.Completed));
        _store.RecordPhase(paths, WorkflowPhase.Simplifier, PhaseStatus.Skipped);
        _store.RecordPhase(paths, WorkflowPhase.Review, PhaseStatus.Skipped);
        _store.RecordPhase(paths, WorkflowPhase.ResolveReview, PhaseStatus.Skipped);

        // Act
        var task = Create().Load(paths);

        // Assert
        Assert.NotNull(task);
        Assert.False(task.JournalCreated);
        Assert.Equal(WorkflowPhase.Implementation, task.ResumePhase);
        var onDisk = _store.TryLoad(paths);
        Assert.NotNull(onDisk);
        Assert.Equal(PhaseStatus.Skipped, onDisk.Phases[(int)WorkflowPhase.Simplifier].Status);
        Assert.Equal(PhaseStatus.Skipped, onDisk.Phases[(int)WorkflowPhase.Review].Status);
        Assert.Equal(PhaseStatus.Skipped, onDisk.Phases[(int)WorkflowPhase.ResolveReview].Status);
    }

    [Fact]
    public void Load_CompletedPhaseWhoseArtefactVanished_IsDemotedAndPersisted()
    {
        // Arrange
        var workspace = NewWorkspace("ws");
        var paths = SeedTask(
            workspace,
            "alpha",
            (WorkflowPhase.Specification, PhaseStatus.Completed),
            (WorkflowPhase.Simplifier, PhaseStatus.Completed),
            (WorkflowPhase.Review, PhaseStatus.Completed));
        File.Delete(paths.ReviewAbsolute);

        // Act
        var task = Create().Load(paths);

        // Assert - demote-never-promote, the same rule and the same write-back as the scan.
        Assert.NotNull(task);
        Assert.Equal(WorkflowPhase.Review, task.ResumePhase);
        Assert.Equal(PhaseStatus.Completed, task.State.Phases[(int)WorkflowPhase.Specification].Status);
        Assert.Equal(PhaseStatus.Completed, task.State.Phases[(int)WorkflowPhase.Simplifier].Status);
        var onDisk = _store.TryLoad(paths);
        Assert.NotNull(onDisk);
        Assert.Equal(PhaseStatus.Pending, onDisk.Phases[(int)WorkflowPhase.Review].Status);
    }

    [Fact]
    public void Load_JournalThatCannotBeWritten_StillReturnsTheReconciledTaskAndLeavesTheFileUnchanged()
    {
        // Arrange - AC-L12, D13: the write-backs are best-effort.
        var workspace = NewWorkspace("ws");
        var paths = SeedTask(
            workspace,
            "alpha",
            (WorkflowPhase.Specification, PhaseStatus.Completed),
            (WorkflowPhase.Simplifier, PhaseStatus.Completed),
            (WorkflowPhase.Review, PhaseStatus.Completed));
        _store.SetDismissed(paths, dismissed: true);
        File.Delete(paths.ReviewAbsolute);
        var bytesBefore = File.ReadAllBytes(paths.StateAbsolute);
        File.SetAttributes(paths.StateAbsolute, FileAttributes.ReadOnly);

        try
        {
            // Act
            var task = Create().Load(paths);

            // Assert
            Assert.NotNull(task);
            Assert.Equal(WorkflowPhase.Review, task.ResumePhase);
            Assert.Equal(PhaseStatus.Pending, task.State.Phases[(int)WorkflowPhase.Review].Status);
            Assert.Equal(bytesBefore, File.ReadAllBytes(paths.StateAbsolute));
        }
        finally
        {
            File.SetAttributes(paths.StateAbsolute, FileAttributes.Normal);
        }

        // Positive control: once writable, the same demotion and the dismissed flag are persisted.
        Assert.NotNull(Create().Load(paths));
        var onDisk = _store.TryLoad(paths);
        Assert.NotNull(onDisk);
        Assert.Equal(PhaseStatus.Pending, onDisk.Phases[(int)WorkflowPhase.Review].Status);
        Assert.False(onDisk.Dismissed);
        Assert.Empty(Directory.GetFiles(paths.TaskDirectory, "*.tmp"));
    }

    [Fact]
    public void Load_AfterTrySkipPhases_ResumesAtImplementation()
    {
        // Arrange - Workflow_LoadFix AC-15: the skip is durable, so a fresh load continues after it.
        var workspace = NewWorkspace("ws");
        var paths = SeedTask(workspace, "alpha", (WorkflowPhase.Specification, PhaseStatus.Completed));
        Assert.NotNull(_store.TrySkipPhases(paths, SkippedPhases));

        // Act
        var task = Create().Load(paths);

        // Assert
        Assert.NotNull(task);
        Assert.Equal(WorkflowPhase.Implementation, task.ResumePhase);
        Assert.Equal(PhaseStatus.Skipped, task.State.Phases[(int)WorkflowPhase.Simplifier].Status);
        Assert.Equal(PhaseStatus.Skipped, task.State.Phases[(int)WorkflowPhase.Review].Status);
        Assert.Equal(PhaseStatus.Skipped, task.State.Phases[(int)WorkflowPhase.ResolveReview].Status);
    }

    [Fact]
    public void Load_SpecPlanAndALaterReviewWithoutAJournal_SkipsTheSimplifierAndResumesAtResolveReview()
    {
        // Arrange - a folder from outside this application whose review is already written.
        var paths = new TaskPaths(NewWorkspace("ws"), "reviewed");
        Directory.CreateDirectory(paths.TaskDirectory);
        WriteAt(paths.SpecAbsolute, "spec", Minute(1));
        WriteAt(paths.PlanAbsolute, "plan", Minute(2));
        WriteAt(paths.ReviewAbsolute, "review", Minute(30));

        // Act
        var task = Create().Load(paths);

        // Assert - running the simplifier now would rewrite documents that were already reviewed.
        Assert.NotNull(task);
        Assert.True(task.JournalCreated);
        Assert.Equal(WorkflowPhase.ResolveReview, task.ResumePhase);
        var onDisk = _store.TryLoad(paths);
        Assert.NotNull(onDisk);
        Assert.Equal(PhaseStatus.Completed, onDisk.Phases[(int)WorkflowPhase.Specification].Status);
        Assert.Equal(PhaseStatus.Skipped, onDisk.Phases[(int)WorkflowPhase.Simplifier].Status);
        Assert.Equal(Minute(2), onDisk.Phases[(int)WorkflowPhase.Simplifier].CompletedUtc);
        Assert.Equal(PhaseStatus.Completed, onDisk.Phases[(int)WorkflowPhase.Review].Status);
        Assert.Equal(Minute(30), onDisk.Phases[(int)WorkflowPhase.Review].CompletedUtc);
        Assert.Equal(PhaseStatus.Pending, onDisk.Phases[(int)WorkflowPhase.ResolveReview].Status);
    }

    [Fact]
    public async Task ScanAsync_LegacyFourEntryJournalOfAFinishedTask_IsNotOfferedAndLoadsWithoutAResumePhase()
    {
        // Arrange - hand-written in the shape a build without the simplifier phase saved. UpdatedUtc
        // is now, so only the finished state - never the 14-day window - can keep it out of the scan.
        var workspace = NewWorkspace("ws");
        var paths = new TaskPaths(workspace, "legacy");
        Directory.CreateDirectory(paths.TaskDirectory);
        await File.WriteAllTextAsync(paths.SpecAbsolute, "spec");
        await File.WriteAllTextAsync(paths.PlanAbsolute, "plan");
        await File.WriteAllTextAsync(paths.ReviewAbsolute, "review");
        await File.WriteAllTextAsync(paths.DoneAbsolute, "done");
        var updated = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        await File.WriteAllTextAsync(paths.StateAbsolute, $$"""
        {
          "version": 1,
          "taskDescription": "alte Aufgabe",
          "createdUtc": "{{updated}}",
          "updatedUtc": "{{updated}}",
          "dismissed": false,
          "phases": [
            { "phase": "Specification",  "status": "Completed", "completedUtc": "2026-10-01T15:12:00Z" },
            { "phase": "Review",         "status": "Completed", "completedUtc": "2026-10-01T15:31:00Z" },
            { "phase": "ResolveReview",  "status": "Completed", "completedUtc": "2026-10-01T15:45:00Z" },
            { "phase": "Implementation", "status": "Completed", "completedUtc": "2026-10-01T16:31:00Z" }
          ]
        }
        """);

        // Act
        var found = await Create().ScanAsync(CancellationToken.None);
        var task = Create().Load(paths);

        // Assert - the load is the positive control that the journal was read at all.
        Assert.Empty(found);
        Assert.NotNull(task);
        Assert.Equal("alte Aufgabe", task.State.TaskDescription);
        Assert.Null(task.ResumePhase);
        Assert.Equal(PhaseStatus.Skipped, task.State.Phases[(int)WorkflowPhase.Simplifier].Status);
        Assert.Equal(PhaseStatus.Completed, task.State.Phases[(int)WorkflowPhase.Implementation].Status);
    }

    [Fact]
    public void CanLoad_FolderWithoutAJournal_IsTrueAndWritesNothing()
    {
        // Arrange - Workflow_LoadFix spec 6.2: the preflight of Load, which here would create a journal.
        var paths = new TaskPaths(NewWorkspace("ws"), "planned");
        Directory.CreateDirectory(paths.TaskDirectory);
        File.WriteAllText(paths.SpecAbsolute, "spec");
        File.WriteAllText(paths.PlanAbsolute, "plan");
        var before = SnapshotFolder(paths.TaskDirectory);

        // Act
        var canLoad = Create().CanLoad(paths);

        // Assert
        Assert.True(canLoad);
        Assert.Equal(before, SnapshotFolder(paths.TaskDirectory));
    }

    [Fact]
    public void CanLoad_ReadableJournal_IsTrue()
    {
        // Arrange - a vanished plan and the dismissed flag would both make Load write; CanLoad must not.
        var paths = SeedTask(NewWorkspace("ws"), "alpha", (WorkflowPhase.Specification, PhaseStatus.Completed));
        _store.SetDismissed(paths, dismissed: true);
        File.Delete(paths.PlanAbsolute);
        var before = SnapshotFolder(paths.TaskDirectory);

        // Act
        var canLoad = Create().CanLoad(paths);

        // Assert
        Assert.True(canLoad);
        Assert.Equal(before, SnapshotFolder(paths.TaskDirectory));
    }

    [Fact]
    public void CanLoad_InvalidJson_IsFalse()
    {
        // Arrange - Workflow_LoadFix D21: no file is renamed for a load that cannot succeed.
        var paths = new TaskPaths(NewWorkspace("ws"), "broken");
        Directory.CreateDirectory(paths.TaskDirectory);
        File.WriteAllText(paths.StateAbsolute, "{ this is not json");
        File.WriteAllText(paths.SpecAbsolute, "spec");
        var before = SnapshotFolder(paths.TaskDirectory);

        // Act
        var canLoad = Create().CanLoad(paths);

        // Assert
        Assert.False(canLoad);
        Assert.Equal(before, SnapshotFolder(paths.TaskDirectory));
    }

    [Fact]
    public void CanLoad_NewerSchemaVersion_IsFalse()
    {
        // Arrange - a journal written by a newer build is unreadable to this one.
        var paths = new TaskPaths(NewWorkspace("ws"), "newer");
        Directory.CreateDirectory(paths.TaskDirectory);
        File.WriteAllText(paths.StateAbsolute, $"{{ \"version\": {TaskState.CurrentVersion + 1} }}");
        var before = SnapshotFolder(paths.TaskDirectory);

        // Act
        var canLoad = Create().CanLoad(paths);

        // Assert
        Assert.False(canLoad);
        Assert.Equal(before, SnapshotFolder(paths.TaskDirectory));
    }

    [Fact]
    public void CanLoad_FolderMissing_IsFalseAndCreatesNothing()
    {
        // Arrange - picked, then deleted before the preflight.
        var paths = new TaskPaths(NewWorkspace("ws"), "vanished");

        // Act
        var canLoad = Create().CanLoad(paths);

        // Assert
        Assert.False(canLoad);
        Assert.False(Directory.Exists(paths.TaskDirectory));
    }

    private static string SnapshotFolder(string directory) =>
        string.Join(
            "|",
            Directory.GetFiles(directory)
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => Path.GetFileName(f) + ":" + Convert.ToHexString(File.ReadAllBytes(f))));

    /// <summary>
    /// A real store whose <see cref="TryCreate"/> loses the race: another creator publishes its
    /// journal first, so this call writes nothing and reports false (Workflow_LoadFix spec 5.2).
    /// </summary>
    private sealed class RacingStateStore(TaskStateStore inner) : ITaskStateStore
    {
        private readonly TaskStateStore _inner = inner;

        public TaskState? TryLoad(TaskPaths paths) => _inner.TryLoad(paths);

        public bool TryCreate(TaskPaths paths)
        {
            _inner.SaveDescription(paths, "fremd");
            return false;
        }

        public void SaveDescription(TaskPaths paths, string taskDescription) =>
            _inner.SaveDescription(paths, taskDescription);

        public void RecordPhase(TaskPaths paths, WorkflowPhase phase, PhaseStatus status) =>
            _inner.RecordPhase(paths, phase, status);

        public TaskState? TrySkipPhases(TaskPaths paths, IReadOnlyCollection<WorkflowPhase> phases) =>
            _inner.TrySkipPhases(paths, phases);

        public void ReplacePhases(TaskPaths paths, IReadOnlyList<TaskPhaseState> phases) =>
            _inner.ReplacePhases(paths, phases);

        public void SetDismissed(TaskPaths paths, bool dismissed) => _inner.SetDismissed(paths, dismissed);

        public void SaveSubtaskSettings(TaskPaths paths, bool enabled, string? workflowDirectory) =>
            _inner.SaveSubtaskSettings(paths, enabled, workflowDirectory);

        public void SetImplementationCompletedManually(TaskPaths paths, bool completedManually) =>
            _inner.SetImplementationCompletedManually(paths, completedManually);
    }
}
