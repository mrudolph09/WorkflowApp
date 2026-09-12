using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Workflow.Models;

namespace Workflow.Services;

/// <inheritdoc cref="IAutoAnswerService" />
public sealed class AutoAnswerService : IAutoAnswerService
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly List<(AutoAnswerRule Rule, Regex? Regex)> _compiled = [];

    /// <summary>Creates the service.</summary>
    /// <param name="shippedRulesPath">Path to Assets\autoanswer.rules.json in the output directory.</param>
    /// <param name="overridePath">Optional user override; when it exists it replaces the shipped set.</param>
    public AutoAnswerService(string shippedRulesPath, string? overridePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shippedRulesPath);

        var path = !string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)
            ? overridePath
            : shippedRulesPath;

        RuleSet = Load(path);

        foreach (var rule in RuleSet.Rules)
        {
            Regex? regex = null;
            try
            {
                regex = new Regex(rule.Pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant, MatchTimeout);
            }
            catch (ArgumentException)
            {
                // A user-edited pattern must not take the application down; the rule is skipped.
            }

            _compiled.Add((rule, regex));
        }
    }

    /// <inheritdoc />
    public AutoAnswerRuleSet RuleSet { get; }

    /// <inheritdoc />
    public AutoAnswerRule? Match(string screenText, IReadOnlySet<string> alreadyFiredRuleIds)
    {
        ArgumentNullException.ThrowIfNull(alreadyFiredRuleIds);

        if (string.IsNullOrEmpty(screenText))
        {
            return null;
        }

        foreach (var (rule, regex) in _compiled)
        {
            if (regex is null || alreadyFiredRuleIds.Contains(rule.Id))
            {
                continue;
            }

            try
            {
                if (regex.IsMatch(screenText))
                {
                    return rule;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Catastrophic backtracking in a user-edited pattern; treat as no match.
            }
        }

        return null;
    }

    private static AutoAnswerRuleSet Load(string path)
    {
        var json = File.ReadAllText(path);
        var dto = JsonSerializer.Deserialize<RuleSetDto>(json, JsonOptions)
                  ?? throw new InvalidOperationException($"Die Regeldatei '{path}' ist leer oder ungültig.");

        var rules = (dto.Rules ?? [])
            .Select(r => new AutoAnswerRule(
                r.Id ?? string.Empty,
                r.Pattern ?? string.Empty,
                EscapeDecoder.Decode(r.Send ?? string.Empty),
                r.Description ?? string.Empty))
            .Where(r => r.Id.Length > 0 && r.Pattern.Length > 0)
            .ToList();

        return new AutoAnswerRuleSet(
            dto.Version,
            dto.QuietPeriodMs,
            dto.SettleTimeoutMs,
            dto.MaxAnswersPerPhase,
            rules);
    }

    private sealed class RuleSetDto
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("quietPeriodMs")]
        public int QuietPeriodMs { get; set; } = 1500;

        [JsonPropertyName("settleTimeoutMs")]
        public int SettleTimeoutMs { get; set; } = 60000;

        [JsonPropertyName("maxAnswersPerPhase")]
        public int MaxAnswersPerPhase { get; set; } = 5;

        [JsonPropertyName("rules")]
        public List<RuleDto>? Rules { get; set; }
    }

    private sealed class RuleDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("pattern")]
        public string? Pattern { get; set; }

        [JsonPropertyName("send")]
        public string? Send { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }
    }
}
