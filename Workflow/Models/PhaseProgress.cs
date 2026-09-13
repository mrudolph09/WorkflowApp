namespace Workflow.Models;

/// <summary>A phase's status change, reported to the UI.</summary>
/// <param name="Phase">The phase whose status changed.</param>
/// <param name="Status">The new status.</param>
public sealed record PhaseProgress(WorkflowPhase Phase, PhaseStatus Status);
