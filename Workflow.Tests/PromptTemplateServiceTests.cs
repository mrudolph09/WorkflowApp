using System.IO;
using System.Text;
using Workflow.Models;
using Workflow.Services;

namespace Workflow.Tests;

public sealed class PromptTemplateServiceTests : IDisposable
{
    private readonly string _dir;

    public PromptTemplateServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wf-prompts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private void WriteTemplate(string name, string content, Encoding? encoding = null)
    {
        File.WriteAllText(
            Path.Combine(_dir, name),
            content,
            encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static Dictionary<string, string> Variables() => new(StringComparer.Ordinal)
    {
        ["taskbezeichnung"] = "my-task",
        ["taskbeschreibung"] = "Beschreibung",
        ["AppDirectory"] = @"C:\src\demo",
        ["spec_path"] = "./my-task/my-task_spec.md",
        ["plan_path"] = "./my-task/my-task_plan.md",
        ["review_path"] = "./my-task/my-task-review.md",
    };

    [Fact]
    public void Render_SubstitutesEveryKnownToken()
    {
        WriteTemplate("t.md", "A {taskbezeichnung} B {taskbeschreibung} C {AppDirectory} D {spec_path} E {plan_path} F {review_path}");
        var service = new PromptTemplateService(_dir);

        var result = service.Render("t.md", Variables());

        Assert.Equal(
            @"A my-task B Beschreibung C C:\src\demo D ./my-task/my-task_spec.md E ./my-task/my-task_plan.md F ./my-task/my-task-review.md",
            result);
    }

    [Fact]
    public void Render_SubstitutesRepeatedTokens()
    {
        WriteTemplate("t.md", "{spec_path} and again {spec_path}");
        var service = new PromptTemplateService(_dir);

        Assert.Equal(
            "./my-task/my-task_spec.md and again ./my-task/my-task_spec.md",
            service.Render("t.md", Variables()));
    }

    [Fact]
    public void Render_PreservesMultiLineDescriptions()
    {
        WriteTemplate("t.md", "{taskbeschreibung}");
        var service = new PromptTemplateService(_dir);
        var variables = Variables();
        variables["taskbeschreibung"] = "Zeile eins\nZeile zwei";

        Assert.Equal("Zeile eins\nZeile zwei", service.Render("t.md", variables));
    }

    [Fact]
    public void Render_ThrowsAndNamesTheUnresolvedToken()
    {
        WriteTemplate("t.md", "hello {unknown_token} world");
        var service = new PromptTemplateService(_dir);

        var ex = Assert.Throws<PromptTemplateException>(() => service.Render("t.md", Variables()));

        Assert.Contains("unknown_token", ex.Message, StringComparison.Ordinal);
        Assert.Contains("t.md", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ThrowsOnEmptyTemplate()
    {
        WriteTemplate("t.md", "   \r\n  ");
        var service = new PromptTemplateService(_dir);

        Assert.Throws<PromptTemplateException>(() => service.Render("t.md", Variables()));
    }

    [Fact]
    public void Render_ThrowsOnMissingFile()
    {
        var service = new PromptTemplateService(_dir);

        Assert.Throws<PromptTemplateException>(() => service.Render("nope.md", Variables()));
    }

    [Fact]
    public void Render_ToleratesAByteOrderMark()
    {
        WriteTemplate("t.md", "X {spec_path}", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var service = new PromptTemplateService(_dir);

        var result = service.Render("t.md", Variables());

        Assert.Equal("X ./my-task/my-task_spec.md", result);
        Assert.DoesNotContain('\uFEFF', result);
    }

    [Fact]
    public void ValidateAll_ReportsEmptyAndUnknownTokenTemplates()
    {
        WriteTemplate("initial_prompt.md", "ok {spec_path}");
        WriteTemplate("review_prompt.md", "");
        WriteTemplate("resolve_review_prompt.md", "ok {plan_path}");
        WriteTemplate("implementation_prompt.md", "bad {nope}");
        var service = new PromptTemplateService(_dir);

        var errors = service.ValidateAll();

        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Contains("review_prompt.md", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("nope", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateAll_AcceptsTheShippedTemplates()
    {
        var shipped = Path.Combine(AppContext.BaseDirectory, "Prompt");
        var service = new PromptTemplateService(shipped);

        Assert.Empty(service.ValidateAll());
    }

    // --- Assertions over the SHIPPED templates (acceptance criterion A8). -------------------
    // Steps 1-3 of this task are one-off edits; without these, a regression in either direction
    // is only discovered by a stalled pipeline at run time.

    private static string Shipped(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Prompt", name));

    [Fact]
    public void ShippedImplementationPrompt_HasNoStrayUnderscoreAfterThePlanToken()
    {
        Assert.DoesNotContain("{plan_path}_", Shipped("implementation_prompt.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void ShippedImplementationPrompt_DoesNotOrderWorkInQDocImport()
    {
        // Spec section 14 / 3.2: QDocImport eval gates are out of scope. The prompt is pasted
        // into the phase-4 agent, so the instruction has to be gone from the file, not merely
        // declared Not Applicable in the specification.
        Assert.DoesNotContain("qdocimport", Shipped("implementation_prompt.md"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShippedImplementationPrompt_PointsAtTheLocalAcceptanceGate()
    {
        Assert.Contains("verify.ps1", Shipped("implementation_prompt.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void ShippedResolveReviewPrompt_GuaranteesADetectableWrite()
    {
        // Phase 3 completes on a content change; a resolver that rejects every finding must
        // still write something or the phase never advances.
        var text = Shipped("resolve_review_prompt.md");

        Assert.NotEmpty(text.Trim());
        Assert.Contains("## Review resolution", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ShippedReviewPrompt_BuildsItsOutputPathFromTheReviewPathToken()
    {
        var text = Shipped("review_prompt.md");

        Assert.Contains("{review_path}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{taskbeschreibung}-review", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PromptVariables_ForBuildsTheCompleteDictionary()
    {
        var paths = new TaskPaths(@"C:\src\demo", "my-task");

        var variables = PromptVariables.For(paths, "beschreibung");

        Assert.Equal(PromptVariables.KnownNames.Count, variables.Count);
        Assert.Equal("my-task", variables["taskbezeichnung"]);
        Assert.Equal("beschreibung", variables["taskbeschreibung"]);
        Assert.Equal(@"C:\src\demo", variables["AppDirectory"]);
        Assert.Equal("./my-task/my-task-review.md", variables["review_path"]);
    }
}
