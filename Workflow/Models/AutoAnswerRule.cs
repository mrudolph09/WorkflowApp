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
/// <param name="SubmitVerifyMs">How long to wait for output proving the carriage return was accepted.</param>
/// <param name="MaxSubmitAttempts">Total carriage returns written, including the first.</param>
public sealed record AutoAnswerRuleSet(
    int Version,
    int QuietPeriodMs,
    int SettleTimeoutMs,
    int MaxAnswersPerPhase,
    IReadOnlyList<AutoAnswerRule> Rules,
    int PasteQuietPeriodMs = 800,
    int PasteSettleTimeoutMs = 15000,
    int SubmitVerifyMs = 1500,
    int MaxSubmitAttempts = 2);
