namespace Workflow.Models;

/// <summary>One screen-text pattern and the key sequence to send when it matches.</summary>
/// <param name="Id">Stable identifier; a rule fires at most once per phase.</param>
/// <param name="Pattern">.NET regular expression matched against the rendered screen text.</param>
/// <param name="Send">Key sequence to write to the pseudo-console, already escape-decoded.</param>
/// <param name="Description">Why this rule exists and why this key sequence is correct.</param>
public sealed record AutoAnswerRule(string Id, string Pattern, string Send, string Description);

/// <summary>The complete auto-answer configuration.</summary>
/// <param name="Version">Schema version of the file.</param>
/// <param name="QuietPeriodMs">Milliseconds of terminal silence that count as "settled".</param>
/// <param name="SettleTimeoutMs">Hard ceiling for the whole settle-and-answer loop.</param>
/// <param name="MaxAnswersPerPhase">Maximum number of automatic answers in one phase.</param>
/// <param name="Rules">Rules in evaluation order; the first match wins.</param>
/// <param name="PasteQuietPeriodMs">Terminal silence after the paste block that counts as "drained".</param>
/// <param name="PasteSettleTimeoutMs">Ceiling on waiting for that silence.</param>
/// <param name="SubmitVerifyMs">How long to wait after each carriage return before checking whether the prompt was submitted.</param>
/// <param name="MaxSubmitAttempts">Total carriage returns written, including the first.</param>
/// <param name="ReadyPattern">
/// Regular expression that identifies the launcher's interactive input being ready to receive the
/// prompt. Until the rendered screen matches it, a quiet terminal with no matching answer rule is
/// treated as "still starting up", not as "ready" — this is what stops the prompt being pasted
/// into a shell while Claude Code / Codex is still cold-starting (SPEC section 7.3).
/// </param>
/// <param name="PastePendingPattern">
/// Regular expression that identifies an actual collapsed-paste chip on screen (Claude Code
/// collapses a multi-line paste to "[Pasted text #1 +N lines]"). On the positive-evidence path it is
/// landing evidence only: it proves the paste reached the launcher, never that it was submitted -
/// the chip stays in the history after a submit. The legacy path still presses Enter until it is
/// gone (SPEC section 6.5).
/// </param>
/// <param name="SubmittedPattern">
/// Regular expression that identifies the launcher actively working on a prompt (Claude Code renders
/// "esc to interrupt" only while a query runs). On the positive-evidence path its false-to-true
/// transition after the one Enter is the only accepted proof of submission. Empty keeps the legacy
/// submit loop for every phase.
/// </param>
/// <param name="SubmitAfterPasteLandedMs">
/// Wait between the paste demonstrably landing and the one Enter, so Claude Code's post-paste
/// handling has finished before the key arrives.
/// </param>
/// <param name="SubmittedVerifyTimeoutMs">How long to look for <paramref name="SubmittedPattern"/> after the Enter.</param>
public sealed record AutoAnswerRuleSet(
    int Version,
    int QuietPeriodMs,
    int SettleTimeoutMs,
    int MaxAnswersPerPhase,
    IReadOnlyList<AutoAnswerRule> Rules,
    int PasteQuietPeriodMs = 800,
    int PasteSettleTimeoutMs = 15000,
    int SubmitVerifyMs = 1500,
    int MaxSubmitAttempts = 4,
    string ReadyPattern = "",
    string PastePendingPattern = "",
    string SubmittedPattern = "",
    int SubmitAfterPasteLandedMs = 8000,
    int SubmittedVerifyTimeoutMs = 10000);
