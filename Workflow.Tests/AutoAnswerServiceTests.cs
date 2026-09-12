using System.IO;
using Workflow.Models;
using Workflow.Services;

namespace Workflow.Tests;

public sealed class AutoAnswerServiceTests : IDisposable
{
    private readonly string _dir;

    public AutoAnswerServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wf-rules-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string WriteRules(string json)
    {
        var path = Path.Combine(_dir, "autoanswer.rules.json");
        File.WriteAllText(path, json);
        return path;
    }

    private const string TwoRules = """
    {
      "version": 1,
      "quietPeriodMs": 1500,
      "settleTimeoutMs": 60000,
      "maxAnswersPerPhase": 5,
      "rules": [
        { "id": "first",  "pattern": "(?is)alpha", "send": "1\\r", "description": "a" },
        { "id": "second", "pattern": "(?is)beta",  "send": "2\\r", "description": "b" }
      ]
    }
    """;

    [Fact]
    public void RuleSet_IsLoadedFromJson()
    {
        var service = new AutoAnswerService(WriteRules(TwoRules), overridePath: null);

        Assert.Equal(1, service.RuleSet.Version);
        Assert.Equal(1500, service.RuleSet.QuietPeriodMs);
        Assert.Equal(60000, service.RuleSet.SettleTimeoutMs);
        Assert.Equal(5, service.RuleSet.MaxAnswersPerPhase);
        Assert.Equal(2, service.RuleSet.Rules.Count);
    }

    [Fact]
    public void Match_ReturnsTheFirstMatchingRuleInFileOrder()
    {
        var service = new AutoAnswerService(WriteRules(TwoRules), overridePath: null);

        var rule = service.Match("... alpha and beta ...", new HashSet<string>(StringComparer.Ordinal));

        Assert.NotNull(rule);
        Assert.Equal("first", rule!.Id);
    }

    [Fact]
    public void Match_SkipsRulesThatAlreadyFired()
    {
        var service = new AutoAnswerService(WriteRules(TwoRules), overridePath: null);

        var rule = service.Match("... alpha and beta ...", new HashSet<string>(StringComparer.Ordinal) { "first" });

        Assert.NotNull(rule);
        Assert.Equal("second", rule!.Id);
    }

    [Fact]
    public void Match_ReturnsNullWhenNothingMatches()
    {
        var service = new AutoAnswerService(WriteRules(TwoRules), overridePath: null);

        Assert.Null(service.Match("gamma", new HashSet<string>(StringComparer.Ordinal)));
    }

    [Fact]
    public void Match_ReturnsNullForEmptyScreenText()
    {
        var service = new AutoAnswerService(WriteRules(TwoRules), overridePath: null);

        Assert.Null(service.Match(string.Empty, new HashSet<string>(StringComparer.Ordinal)));
    }

    [Fact]
    public void Match_IgnoresARuleWithAMalformedRegexInsteadOfThrowing()
    {
        var json = """
        {
          "version": 1, "quietPeriodMs": 1, "settleTimeoutMs": 1, "maxAnswersPerPhase": 1,
          "rules": [
            { "id": "broken", "pattern": "([unclosed", "send": "x", "description": "" },
            { "id": "good",   "pattern": "hello",     "send": "y", "description": "" }
          ]
        }
        """;
        var service = new AutoAnswerService(WriteRules(json), overridePath: null);

        var rule = service.Match("hello", new HashSet<string>(StringComparer.Ordinal));

        Assert.NotNull(rule);
        Assert.Equal("good", rule!.Id);
    }

    [Fact]
    public void OverrideFile_ReplacesTheShippedRulesWholesale()
    {
        var shipped = WriteRules(TwoRules);
        var overridePath = Path.Combine(_dir, "override.json");
        File.WriteAllText(overridePath, """
        {
          "version": 2, "quietPeriodMs": 100, "settleTimeoutMs": 200, "maxAnswersPerPhase": 9,
          "rules": [ { "id": "only", "pattern": "zeta", "send": "z", "description": "" } ]
        }
        """);

        var service = new AutoAnswerService(shipped, overridePath);

        Assert.Equal(2, service.RuleSet.Version);
        Assert.Single(service.RuleSet.Rules);
        Assert.Equal("only", service.RuleSet.Rules[0].Id);
    }

    [Fact]
    public void Send_IsEscapeDecodedWhenTheRuleIsRead()
    {
        var service = new AutoAnswerService(WriteRules(TwoRules), overridePath: null);

        Assert.Equal("1\r", service.RuleSet.Rules[0].Send);
    }

    [Fact]
    public void ShippedRules_AnswerTheClaudeBypassWarningWithTwoNotEnter()
    {
        var shipped = Path.Combine(AppContext.BaseDirectory, "Assets", "autoanswer.rules.json");
        var service = new AutoAnswerService(shipped, overridePath: null);

        var screen = "WARNING: Claude Code running in Bypass Permissions mode\n  1. No, exit\n> 2. Yes, I accept";
        var rule = service.Match(screen, new HashSet<string>(StringComparer.Ordinal));

        Assert.NotNull(rule);
        Assert.Equal("claude-bypass-permissions", rule!.Id);
        Assert.Equal("2\r", rule.Send);
    }

    [Fact]
    public void ShippedRules_AnswerTheTrustFolderDialogWithEnter()
    {
        var shipped = Path.Combine(AppContext.BaseDirectory, "Assets", "autoanswer.rules.json");
        var service = new AutoAnswerService(shipped, overridePath: null);

        var rule = service.Match("Do you trust the files in this folder?", new HashSet<string>(StringComparer.Ordinal));

        Assert.NotNull(rule);
        Assert.Equal("claude-trust-folder", rule!.Id);
        Assert.Equal("\r", rule.Send);
    }
}
