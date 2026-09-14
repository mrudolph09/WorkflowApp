namespace Workflow.Models;

/// <summary>One of the four stations of the workflow pipeline.</summary>
public enum WorkflowPhase
{
    /// <summary>Station 1 - Spezifikation.</summary>
    Specification = 0,

    /// <summary>Station 2 - Review.</summary>
    Review = 1,

    /// <summary>Station 3 - Review umsetzen.</summary>
    ResolveReview = 2,

    /// <summary>Station 4 - Implementierung.</summary>
    Implementation = 3,
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
