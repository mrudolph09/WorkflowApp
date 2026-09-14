namespace Workflow.Models;

/// <summary>One interrupted task found by the startup scan.</summary>
/// <param name="Paths">The task's path set, derived from the folder that held the journal.</param>
/// <param name="State">The journal, already reconciled against the artefacts on disk.</param>
/// <param name="ResumePhase">The first phase that is not yet completed.</param>
public sealed record RecoverableTask(TaskPaths Paths, TaskState State, WorkflowPhase ResumePhase);
