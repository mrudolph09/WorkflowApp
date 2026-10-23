namespace Workflow.Models;

/// <summary>
/// One task to open as a prefilled tab: found by the startup scan, or picked with <c>Task laden</c>.
/// </summary>
/// <param name="Paths">The task's path set, derived from the folder that held the journal.</param>
/// <param name="State">The journal, already reconciled against the artefacts on disk.</param>
/// <param name="ResumePhase">
/// The first phase that is neither completed nor skipped, or null when every phase is done. Only
/// <c>Task laden</c> produces null: the startup scan never offers a finished task.
/// </param>
/// <param name="JournalCreated">
/// True when <c>Task laden</c> found no journal in the folder and wrote a fresh one before reading it
/// (Workflow_LoadFix spec section 5). Defaulted, so the startup scan and every existing construction
/// site keep meaning "the journal was already there".
/// </param>
public sealed record RecoverableTask(
    TaskPaths Paths,
    TaskState State,
    WorkflowPhase? ResumePhase,
    bool JournalCreated = false);
