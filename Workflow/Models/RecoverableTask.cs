namespace Workflow.Models;

/// <summary>
/// One task to open as a prefilled tab: found by the startup scan, or picked with <c>Task laden</c>.
/// </summary>
/// <param name="Paths">The task's path set, derived from the folder that held the journal.</param>
/// <param name="State">The journal, already reconciled against the artefacts on disk.</param>
/// <param name="ResumePhase">
/// The first phase that is not yet completed, or null when every phase is. Only <c>Task laden</c>
/// produces null: the startup scan never offers a finished task.
/// </param>
public sealed record RecoverableTask(TaskPaths Paths, TaskState State, WorkflowPhase? ResumePhase);
