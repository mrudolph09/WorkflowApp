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
    public void ShippedRules_AnswerTheClaudeBypassWarningWithArrowDownThenEnter()
    {
        var shipped = Path.Combine(AppContext.BaseDirectory, "Assets", "autoanswer.rules.json");
        var service = new AutoAnswerService(shipped, overridePath: null);

        var screen = "WARNING: Claude Code running in Bypass Permissions mode\n  1. No, exit\n> 2. Yes, I accept";
        var rule = service.Match(screen, new HashSet<string>(StringComparer.Ordinal));

        Assert.NotNull(rule);
        Assert.Equal("claude-bypass-permissions", rule!.Id);
        Assert.Equal("\u001b[B\r", rule.Send);
    }

    [Fact]
    public void ShippedRules_AnswerTheSafetyCheckTrustDialogWithArrowDownThenEnter()
    {
        // Claude Code 2.1.272 renders this instead of "Do you trust the files in this folder?",
        // and preselects "No, exit". Captured from a live PTY on 2026-09-15.
        var shipped = Path.Combine(AppContext.BaseDirectory, "Assets", "autoanswer.rules.json");
        var service = new AutoAnswerService(shipped, overridePath: null);

        var screen =
            "Accessing workspace: C:\\quincy\\windata\\Schnelleingabetafel\n" +
            "Quick safety check: Is this a project you created or one you trust? (Like your own code, a\n" +
            "well-known open source project, or work from your team). If not, take a moment to review\n" +
            "what's in this folder first.\n" +
            "Claude Code'll be able to read, edit, and execute files here.\n" +
            "Security guide\n" +
            "\u276f No, exit\n" +
            "  Yes, I trust this folder\n" +
            "Enter to confirm \u00b7 Esc to cancel";
        var rule = service.Match(screen, new HashSet<string>(StringComparer.Ordinal));

        Assert.NotNull(rule);
        Assert.Equal("claude-trust-folder-safety-check", rule!.Id);
        Assert.Equal("\u001b[B\r", rule.Send);
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

    [Fact]
    public void ShippedRules_AnswerTheCodexDirectoryTrustDialogWithEnter()
    {
        // Codex CLI 0.154 shows this gate in every directory that is not yet listed under
        // [projects.'...'] in ~/.codex/config.toml - `--yolo` does not skip it. Phase 2 launches
        // `codex --yolo`, so this is what a first run in a new task folder meets. Captured from a
        // live pseudo-console on 2026-09-16; the sentence is split across rows because the
        // snapshot is a wrapped 120-column buffer.
        var shipped = Path.Combine(AppContext.BaseDirectory, "Assets", "autoanswer.rules.json");
        var service = new AutoAnswerService(shipped, overridePath: null);

        var screen =
            "\u203a Ask Codex to do anything\n" +
            "  ? for shortcuts\n" +
            "> You are in C:\\quincy\\windata\\Schnelleingabetafel\n" +
            "Do you trust the contents of this directory? Working with untrusted contents comes\n" +
            "with higher risk of prompt injection. Trusting the directory allows project-local\n" +
            "config, hooks, and exec policies to load.\n" +
            "\u203a 1. Yes, continue\n" +
            "  2. No, quit\n" +
            "Press enter to continue";
        var rule = service.Match(screen, new HashSet<string>(StringComparer.Ordinal));

        Assert.NotNull(rule);
        Assert.Equal("codex-trust-directory", rule!.Id);
        Assert.Equal("\r", rule.Send);

        // This rule is the ONLY thing standing between the prompt and that dialog: Codex paints
        // its composer behind the modal, so the footer the ready gate looks for is already on
        // screen and IsLauncherReady is true while the gate still has the keyboard. Unanswered,
        // the settle loop therefore returns at once and the prompt is pasted into the dialog.
        Assert.True(service.IsLauncherReady(screen));
    }

    [Fact]
    public void ShippedRules_TreatTheIdleCodexComposerAsLauncherReady()
    {
        // Captured from a live pseudo-console on 2026-09-16, Codex CLI 0.154.0, after startup has
        // finished and the composer is waiting for input. The hint row older builds painted next to
        // the composer ("? for shortcuts") is NOT on this screen - a model/directory/context status
        // line replaces it - so the shipped readyPattern matched nothing for the whole of phase 2
        // and the settle loop only pasted once its 60 s ceiling expired (SPEC section 7.3).
        var shipped = Path.Combine(AppContext.BaseDirectory, "Assets", "autoanswer.rules.json");
        var service = new AutoAnswerService(shipped, overridePath: null);

        var screen = """
        ╭────────────────────────────────────────────────────╮
        │ >_ OpenAI Codex (v0.154.0)                         │
        │                                                    │
        │ model:       gpt-5.6-sol high   /model to change   │
        │ directory:   C:\quincy\windata\Schnelleingabetafel │
        │ permissions: YOLO mode                             │
        ╰────────────────────────────────────────────────────╯

          Tip: Try the Desktop app. Run 'codex app' or visit https://chatgpt.com/codex

        ⚠ 1 startup issue · ctrl + t for details

        • You have 2 usage limit resets available. Run /usage to use one.

        › Ask Codex to do anything

          gpt-5.6-sol high · C:\quincy\windata\Schnelleingabetafel · Context 0% used
        """;

        Assert.True(service.IsLauncherReady(screen));

        // Nothing on it is a dialog, so the settle loop has nothing left to answer and may paste.
        Assert.Null(service.Match(screen, new HashSet<string>(StringComparer.Ordinal)));
    }

    [Fact]
    public void RuleSet_FileOmitsTheSubmitFields_UsesTheDefaults()
    {
        var path = WriteRules("""
        {
          "version": 1, "quietPeriodMs": 1500, "settleTimeoutMs": 60000,
          "maxAnswersPerPhase": 5, "rules": []
        }
        """);

        var set = new AutoAnswerService(path, overridePath: null).RuleSet;

        Assert.Equal(800, set.PasteQuietPeriodMs);
        Assert.Equal(15000, set.PasteSettleTimeoutMs);
        Assert.Equal(1500, set.SubmitVerifyMs);
        Assert.Equal(2, set.MaxSubmitAttempts);
    }

    [Fact]
    public void RuleSet_FileSetsTheSubmitFields_UsesThem()
    {
        var path = WriteRules("""
        {
          "version": 2, "quietPeriodMs": 1500, "settleTimeoutMs": 60000, "maxAnswersPerPhase": 5,
          "pasteQuietPeriodMs": 10, "pasteSettleTimeoutMs": 200,
          "submitVerifyMs": 20, "maxSubmitAttempts": 3,
          "rules": []
        }
        """);

        var set = new AutoAnswerService(path, overridePath: null).RuleSet;

        Assert.Equal(10, set.PasteQuietPeriodMs);
        Assert.Equal(200, set.PasteSettleTimeoutMs);
        Assert.Equal(20, set.SubmitVerifyMs);
        Assert.Equal(3, set.MaxSubmitAttempts);
    }

    [Fact]
    public void RuleSet_SubmitFieldSetToZero_FallsBackToTheShippedDefault()
    {
        var path = WriteRules("""
        {
          "version": 2, "quietPeriodMs": 1500, "settleTimeoutMs": 60000, "maxAnswersPerPhase": 5,
          "pasteQuietPeriodMs": 0, "submitVerifyMs": -1, "maxSubmitAttempts": 0,
          "rules": []
        }
        """);

        var set = new AutoAnswerService(path, overridePath: null).RuleSet;

        Assert.Equal(800, set.PasteQuietPeriodMs);
        Assert.Equal(1500, set.SubmitVerifyMs);
        Assert.Equal(4, set.MaxSubmitAttempts);
    }

    [Fact]
    public void ShippedRules_CarryALauncherReadyAndAPastePendingPattern()
    {
        var shipped = Path.Combine(AppContext.BaseDirectory, "Assets", "autoanswer.rules.json");
        var service = new AutoAnswerService(shipped, overridePath: null);

        // The launcher is only "ready" once Claude Code's own footer is on screen, and a pasted
        // prompt is "pending" while its collapsed chip is shown.
        Assert.True(service.IsLauncherReady("... ? for shortcuts"));
        Assert.True(service.IsLauncherReady("\u23f5\u23f5 bypass permissions on (shift+tab to cycle)"));
        Assert.False(service.IsLauncherReady("C:\\ws> yo"));

        Assert.True(service.IsPastePending("[Pasted text #1 +40 lines] paste again to expand"));
        Assert.False(service.IsPastePending("\u276f "));
    }

    [Fact]
    public void IsLauncherReady_WithoutAPattern_IsAlwaysTrue()
    {
        var path = WriteRules("""
        {
          "version": 2, "quietPeriodMs": 1500, "settleTimeoutMs": 60000, "maxAnswersPerPhase": 5,
          "rules": []
        }
        """);

        var service = new AutoAnswerService(path, overridePath: null);

        Assert.True(service.IsLauncherReady("anything at all"));
        Assert.False(service.IsPastePending("anything at all"));
    }
}
