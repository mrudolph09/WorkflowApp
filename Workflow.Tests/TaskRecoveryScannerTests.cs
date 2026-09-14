using System.IO;
using Workflow.Models;
using Workflow.Services;

namespace Workflow.Tests;

public sealed class TaskRecoveryScannerTests : IDisposable
{
    private readonly string _root;
    private readonly SettingsService _settings;
    private readonly TaskStateStore _store = new();

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
        Assert.Equal(WorkflowPhase.Review, task.ResumePhase);
        Assert.Equal("Beschreibung von alpha", task.State.TaskDescription);
    }

    [Fact]
    public async Task ScanAsync_FolderWithoutAJournal_IsIgnored()
    {
        var workspace = NewWorkspace("ws");
        Directory.CreateDirectory(Path.Combine(workspace, "not-a-task"));

        Assert.Empty(await Create().ScanAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ScanAsync_AllPhasesCompleted_IsNotOffered()
    {
        var workspace = NewWorkspace("ws");
        SeedTask(
            workspace,
            "done",
            (WorkflowPhase.Specification, PhaseStatus.Completed),
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
            (WorkflowPhase.Review, PhaseStatus.Completed),
            (WorkflowPhase.ResolveReview, PhaseStatus.Completed));

        // The plan artefact disappears, so phase 1 - and therefore 2, 3 and 4 - is demoted.
        File.Delete(paths.PlanAbsolute);

        var first = Assert.Single(await Create().ScanAsync(CancellationToken.None));
        Assert.Equal(WorkflowPhase.Specification, first.ResumePhase);

        // The resumed run finishes phase 1, then the app dies again. RecordPhase touches ONLY
        // phase 1, so everything after it must already have been cleared on disk by the scan.
        await File.WriteAllTextAsync(paths.PlanAbsolute, "plan");
        _store.RecordPhase(paths, WorkflowPhase.Specification, PhaseStatus.Completed);

        var second = Assert.Single(await Create().ScanAsync(CancellationToken.None));

        // T-review.md is still on disk, so nothing demotes phase 2 this time round. Had the first
        // scan reconciled in memory only, the journal would still claim phases 2 and 3 are
        // Completed and this would read Implementation - silently skipping two phases
        // (SPEC 6.3.1, R13, D17).
        Assert.Equal(WorkflowPhase.Review, second.ResumePhase);
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
    public async Task ScanAsync_PhaseThreeCompleted_IsNeverDemotedByDiskEvidence()
    {
        var workspace = NewWorkspace("ws");
        SeedTask(
            workspace,
            "alpha",
            (WorkflowPhase.Specification, PhaseStatus.Completed),
            (WorkflowPhase.Review, PhaseStatus.Completed),
            (WorkflowPhase.ResolveReview, PhaseStatus.Completed));

        var task = Assert.Single(await Create().ScanAsync(CancellationToken.None));

        Assert.Equal(WorkflowPhase.Implementation, task.ResumePhase);
    }
}
