using Workflow.Models;

namespace Workflow.Services;

/// <summary>Matches rendered terminal text against the configured auto-answer rules.</summary>
public interface IAutoAnswerService
{
    /// <summary>The loaded configuration.</summary>
    public AutoAnswerRuleSet RuleSet { get; }

    /// <summary>Finds the first rule that matches and has not fired yet in this phase.</summary>
    /// <param name="screenText">The last rendered rows of the terminal.</param>
    /// <param name="alreadyFiredRuleIds">Rule identifiers already used in this phase.</param>
    /// <returns>The matching rule, or null.</returns>
    public AutoAnswerRule? Match(string screenText, IReadOnlySet<string> alreadyFiredRuleIds);

    /// <summary>
    /// True when the rendered screen shows the launcher's interactive input is ready to accept the
    /// prompt. When no ready pattern is configured this returns true so behaviour is unchanged.
    /// </summary>
    /// <param name="screenText">The last rendered rows of the terminal.</param>
    /// <returns>Whether the prompt may now be pasted.</returns>
    public bool IsLauncherReady(string screenText);

    /// <summary>
    /// True when the rendered screen still shows a pasted-but-unsubmitted prompt in the input box.
    /// When no pattern is configured this returns false, so the submit loop keeps its previous
    /// output-based verification.
    /// </summary>
    /// <param name="screenText">The last rendered rows of the terminal.</param>
    /// <returns>Whether an unsubmitted paste is still in the input.</returns>
    public bool IsPastePending(string screenText);
}
