using System.Collections.ObjectModel;
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

    // Every method guards its argument. AnalysisMode=All applies to this project too, so CA1062
    // is an error wherever a public method dereferences a parameter it did not null-check.
    public TaskState? TryLoad(TaskPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return _states.TryGetValue(paths.TaskDirectory, out var state) ? state : null;
    }

    public void SaveDescription(TaskPaths paths, string taskDescription)
    {
        ArgumentNullException.ThrowIfNull(paths);

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
            phase, status, status == PhaseStatus.Completed ? DateTimeOffset.UtcNow : null);
        _states[paths.TaskDirectory] = state;
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
