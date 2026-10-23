using System.Collections.ObjectModel;
using System.IO;
using Workflow.Models;
using Workflow.Services;

namespace Workflow.Tests.Fakes;

/// <summary>An in-memory journal that records every call the orchestrator makes.</summary>
public sealed class FakeTaskStateStore : ITaskStateStore
{
    private readonly Dictionary<string, TaskState> _states = new(StringComparer.OrdinalIgnoreCase);

    public Collection<(WorkflowPhase Phase, PhaseStatus Status)> Recorded { get; } = [];

    public Collection<string> Descriptions { get; } = [];

    public Collection<(string Directory, bool Dismissed)> Dismissals { get; } = [];

    public Collection<(string Directory, IReadOnlyList<TaskPhaseState> Phases)> Replacements { get; } = [];

    public Collection<(string Directory, bool Enabled, string? WorkflowDirectory)> SubtaskSettings { get; } = [];

    public Collection<(string Directory, bool CompletedManually)> ManualCompletions { get; } = [];

    public Collection<string> Creations { get; } = [];

    public Collection<(string Directory, WorkflowPhase[] Phases)> SkipRequests { get; } = [];

    /// <summary>While set, <see cref="TrySkipPhases"/> changes nothing and returns null - a write that did not land.</summary>
    public bool FailSkipWrites { get; set; }

    /// <summary>
    /// While equal to a task's name, <see cref="SaveDescription"/> throws an <see cref="IOException"/>
    /// before recording anything - a description write that did not land (WF_QUEUE spec Q14).
    /// </summary>
    public string? FailDescriptionWriteFor { get; set; }

    // Every method guards its argument. AnalysisMode=All applies to this project too, so CA1062
    // is an error wherever a public method dereferences a parameter it did not null-check.
    public TaskState? TryLoad(TaskPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return _states.TryGetValue(paths.TaskDirectory, out var state) ? state : null;
    }

    public bool TryCreate(TaskPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        // Mirrors the real store: an existing journal is never replaced.
        if (_states.ContainsKey(paths.TaskDirectory))
        {
            return false;
        }

        Creations.Add(paths.TaskDirectory);
        _states[paths.TaskDirectory] = NewState();
        return true;
    }

    public void SaveDescription(TaskPaths paths, string taskDescription)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (string.Equals(FailDescriptionWriteFor, paths.TaskName, StringComparison.Ordinal))
        {
            throw new IOException("simulated description write failure");
        }

        Descriptions.Add(taskDescription);
        var state = TryLoad(paths) ?? NewState();
        state.TaskDescription = taskDescription;
        state.Dismissed = false;
        _states[paths.TaskDirectory] = state;
    }

    public void RecordPhase(TaskPaths paths, WorkflowPhase phase, PhaseStatus status)
    {
        ArgumentNullException.ThrowIfNull(paths);

        Recorded.Add((phase, status));
        var state = TryLoad(paths) ?? NewState();
        state.Phases[(int)phase] = new TaskPhaseState(
            phase,
            status,
            status is PhaseStatus.Completed or PhaseStatus.Skipped ? DateTimeOffset.UtcNow : null);
        _states[paths.TaskDirectory] = state;
    }

    public TaskState? TrySkipPhases(TaskPaths paths, IReadOnlyCollection<WorkflowPhase> phases)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(phases);

        SkipRequests.Add((paths.TaskDirectory, [.. phases]));

        var state = TryLoad(paths);
        if (FailSkipWrites || state is null)
        {
            // Mirrors the real store: no journal is invented, and a failed write changes nothing.
            return null;
        }

        // Mirrors the real store: one shared stamp, and a phase already done keeps its entry.
        var now = DateTimeOffset.UtcNow;
        foreach (var phase in phases)
        {
            if (!PhaseReconciliation.IsDone(state.Phases[(int)phase].Status))
            {
                state.Phases[(int)phase] = new TaskPhaseState(phase, PhaseStatus.Skipped, now);
            }
        }

        var readBack = TryLoad(paths);
        return readBack is not null && phases.All(p => PhaseReconciliation.IsDone(readBack.Phases[(int)p].Status))
            ? readBack
            : null;
    }

    public void ReplacePhases(TaskPaths paths, IReadOnlyList<TaskPhaseState> phases)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(phases);

        Replacements.Add((paths.TaskDirectory, phases));

        var state = TryLoad(paths);
        if (state is null)
        {
            // Mirrors the real store: no journal, nothing to correct.
            return;
        }

        state.Phases.Clear();
        foreach (var entry in phases)
        {
            state.Phases.Add(entry);
        }
    }

    public void SetDismissed(TaskPaths paths, bool dismissed)
    {
        ArgumentNullException.ThrowIfNull(paths);

        Dismissals.Add((paths.TaskDirectory, dismissed));
        var state = TryLoad(paths);
        if (state is not null)
        {
            state.Dismissed = dismissed;
        }
    }

    public void SaveSubtaskSettings(TaskPaths paths, bool enabled, string? workflowDirectory)
    {
        ArgumentNullException.ThrowIfNull(paths);

        SubtaskSettings.Add((paths.TaskDirectory, enabled, workflowDirectory));
        var state = TryLoad(paths) ?? NewState();
        state.SubtasksEnabled = enabled;
        state.WorkflowDirectory = workflowDirectory;
        _states[paths.TaskDirectory] = state;
    }

    public void SetImplementationCompletedManually(TaskPaths paths, bool completedManually)
    {
        ArgumentNullException.ThrowIfNull(paths);

        ManualCompletions.Add((paths.TaskDirectory, completedManually));
        var state = TryLoad(paths);
        if (state is not null)
        {
            // Mirrors the real store: no journal, nothing to override.
            state.ImplementationCompletedManually = completedManually;
        }
    }

    /// <summary>Seeds a journal so a test can exercise the recovery paths.</summary>
    public void Seed(TaskPaths paths, TaskState state)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _states[paths.TaskDirectory] = state;
    }

    private static TaskState NewState()
    {
        var state = new TaskState { CreatedUtc = DateTimeOffset.UtcNow, UpdatedUtc = DateTimeOffset.UtcNow };

        foreach (var definition in PhaseCatalog.All)
        {
            state.Phases.Add(new TaskPhaseState(definition.Phase, PhaseStatus.Pending, null));
        }

        return state;
    }
}
