namespace Workflow.Models;

/// <summary>The five phase definitions, in pipeline order.</summary>
public static class PhaseCatalog
{
    /// <summary>All phases, ordered Specification -> Simplifier -> Review -> ResolveReview -> Implementation.</summary>
    public static IReadOnlyList<PhaseDefinition> All { get; } =
    [
        new PhaseDefinition(
            WorkflowPhase.Specification, "Spezifikation", "yo", "initial_prompt.md", CompletionRule.FilesExist,
            PromptSubmissionMode.PositiveEvidence),
        // The simplifier edits spec and plan in place over many writes, so their hashes cannot say
        // when it is finished. Its report, written as the very last action, is the completion marker.
        new PhaseDefinition(
            WorkflowPhase.Simplifier, "Vereinfachen", "yo", "simplifier_prompt.md", CompletionRule.FilesExist,
            PromptSubmissionMode.PositiveEvidence),
        // Full path: this is where CodexUpdateService's standalone installer puts the CLI, so the
        // review runs the binary the app keeps current - not whichever `codex` (npm, bun) is first
        // on PATH.
        // --no-daemon: the background server is a detached process without a console, so every
        // PowerShell it spawns flashes its own window; its pid file also outlives a reboot, and once
        // Windows hands that pid to a protected process (lsass) `codex` dies with "failed to open
        // daemon process: Zugriff verweigert (os error 5)" before the review ever starts.
        // RetryUntilEvidence: Codex redraws on every Enter, so output proves nothing; Enter is
        // repeated until its working footer appears or its paste placeholder leaves the composer.
        new PhaseDefinition(
            WorkflowPhase.Review,
            "Review",
            @"& ""$env:LOCALAPPDATA\Programs\OpenAI\Codex\bin\codex.exe"" --yolo --no-daemon",
            "review_prompt.md",
            CompletionRule.FilesExist,
            PromptSubmissionMode.RetryUntilEvidence),
        new PhaseDefinition(
            WorkflowPhase.ResolveReview, "Review umsetzen", "yo", "resolve_review_prompt.md", CompletionRule.AllContentChanged,
            PromptSubmissionMode.PositiveEvidence),
        new PhaseDefinition(
            WorkflowPhase.Implementation, "Implementierung", "yo", "implementation_prompt.md", CompletionRule.FilesExist,
            PromptSubmissionMode.PositiveEvidence),
    ];

    /// <summary>Looks up a single phase definition.</summary>
    /// <param name="phase">The phase to look up.</param>
    /// <returns>The matching definition.</returns>
    public static PhaseDefinition For(WorkflowPhase phase) => All[(int)phase];
}
