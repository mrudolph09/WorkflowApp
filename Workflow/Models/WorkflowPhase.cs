namespace Workflow.Models;

/// <summary>
/// One of the five stations of the workflow pipeline. The value is the phase's index in
/// <see cref="PhaseCatalog.All"/>; journals store the name, so renumbering is safe on disk.
/// </summary>
public enum WorkflowPhase
{
    /// <summary>Station 1 - Spezifikation.</summary>
    Specification = 0,

    /// <summary>Station 2 - Vereinfachen: cuts the fresh spec and plan down before the review.</summary>
    Simplifier = 1,

    /// <summary>Station 3 - Review.</summary>
    Review = 2,

    /// <summary>Station 4 - Review umsetzen.</summary>
    ResolveReview = 3,

    /// <summary>Station 5 - Implementierung.</summary>
    Implementation = 4,
}

/// <summary>Visual state of a single phase indicator.</summary>
public enum PhaseStatus
{
    /// <summary>Grey - the phase has not started.</summary>
    Pending,

    /// <summary>Yellow - the phase is running.</summary>
    Active,

    /// <summary>Green - the phase finished.</summary>
    Completed,

    /// <summary>
    /// Blue-grey - the user moved past the phase with <c>Implementierung beginnen</c> before it ran
    /// to completion. It counts as done for resuming, but it never happened (Workflow_LoadFix spec
    /// section 7).
    /// </summary>
    Skipped,
}

/// <summary>How the orchestrator decides that a phase is finished.</summary>
public enum CompletionRule
{
    /// <summary>Every watched path exists and is non-empty.</summary>
    FilesExist,

    /// <summary>At least one watched path differs from its baseline hash.</summary>
    AnyContentChanged,

    /// <summary>Every watched path differs from its baseline hash.</summary>
    AllContentChanged,
}
