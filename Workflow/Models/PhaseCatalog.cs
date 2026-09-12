namespace Workflow.Models;

/// <summary>The four phase definitions, in pipeline order.</summary>
public static class PhaseCatalog
{
    /// <summary>All phases, ordered Specification -> Review -> ResolveReview -> Implementation.</summary>
    public static IReadOnlyList<PhaseDefinition> All { get; } =
    [
        new PhaseDefinition(
            WorkflowPhase.Specification, "Spezifikation", "yo", "initial_prompt.md", CompletionRule.FilesExist),
        new PhaseDefinition(
            WorkflowPhase.Review, "Review", "codex --yolo", "review_prompt.md", CompletionRule.FilesExist),
        new PhaseDefinition(
            WorkflowPhase.ResolveReview, "Review umsetzen", "yo", "resolve_review_prompt.md", CompletionRule.AnyContentChanged),
        new PhaseDefinition(
            WorkflowPhase.Implementation, "Implementierung", "yo", "implementation_prompt.md", CompletionRule.Manual),
    ];

    /// <summary>Looks up a single phase definition.</summary>
    /// <param name="phase">The phase to look up.</param>
    /// <returns>The matching definition.</returns>
    public static PhaseDefinition For(WorkflowPhase phase) => All[(int)phase];
}
