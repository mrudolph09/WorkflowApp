namespace Workflow.Models;

/// <summary>How the orchestrator decides that a pasted prompt has been submitted.</summary>
/// <remarks>
/// An explicit per-launcher contract, never inferred from screen content: whether a chip is shown or
/// a footer is missing says nothing reliable about which CLI is running.
/// </remarks>
public enum PromptSubmissionMode
{
    /// <summary>
    /// Presses Enter and treats terminal output after it as the submit (SPEC section 6.5). The
    /// fallback for a rules file without a submitted pattern; no launcher declares it.
    /// </summary>
    LegacyOutputActivity,

    /// <summary>
    /// Writes at most one Enter, only once the paste has demonstrably landed, and treats nothing but
    /// the launcher's working footer as proof of submission. For Claude Code, which reads queued
    /// carriage returns as literal line breaks.
    /// </summary>
    PositiveEvidence,

    /// <summary>
    /// Writes one Enter per attempt until the screen proves the prompt was taken: the launcher's
    /// working footer appears, or its collapsed-paste placeholder leaves the input. For Codex, which
    /// redraws on every Enter whether or not it submits, ignores an Enter on an empty input, and
    /// removes its placeholder from the screen on submit.
    /// </summary>
    RetryUntilEvidence,
}
