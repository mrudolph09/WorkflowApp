using Workflow.Models;

namespace Workflow.Tests;

public class PhaseCatalogTests
{
    [Fact]
    public void All_HasFourPhasesInPipelineOrder()
    {
        var all = PhaseCatalog.All;

        Assert.Equal(4, all.Count);
        Assert.Equal(WorkflowPhase.Specification, all[0].Phase);
        Assert.Equal(WorkflowPhase.Review, all[1].Phase);
        Assert.Equal(WorkflowPhase.ResolveReview, all[2].Phase);
        Assert.Equal(WorkflowPhase.Implementation, all[3].Phase);
    }

    [Theory]
    [InlineData(WorkflowPhase.Specification, "Spezifikation", "yo", "initial_prompt.md", CompletionRule.FilesExist)]
    [InlineData(WorkflowPhase.Review, "Review", "codex --yolo", "review_prompt.md", CompletionRule.FilesExist)]
    [InlineData(WorkflowPhase.ResolveReview, "Review umsetzen", "yo", "resolve_review_prompt.md", CompletionRule.AllContentChanged)]
    [InlineData(WorkflowPhase.Implementation, "Implementierung", "yo", "implementation_prompt.md", CompletionRule.FilesExist)]
    public void For_ReturnsTheSpecifiedDefinition(
        WorkflowPhase phase, string displayName, string launcher, string promptFile, CompletionRule rule)
    {
        var definition = PhaseCatalog.For(phase);

        Assert.Equal(displayName, definition.DisplayName);
        Assert.Equal(launcher, definition.Launcher);
        Assert.Equal(promptFile, definition.PromptFile);
        Assert.Equal(rule, definition.Completion);
    }
}
