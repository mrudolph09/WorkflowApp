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
}
