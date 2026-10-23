using Workflow.Models;

namespace Workflow.Tests;

public class PhaseCatalogTests
{
    [Fact]
    public void All_HasFivePhasesInPipelineOrder()
    {
        var all = PhaseCatalog.All;

        Assert.Equal(5, all.Count);
        Assert.Equal(WorkflowPhase.Specification, all[0].Phase);
        Assert.Equal(WorkflowPhase.Simplifier, all[1].Phase);
        Assert.Equal(WorkflowPhase.Review, all[2].Phase);
        Assert.Equal(WorkflowPhase.ResolveReview, all[3].Phase);
        Assert.Equal(WorkflowPhase.Implementation, all[4].Phase);
    }

    [Fact]
    public void All_EveryPhaseSitsAtTheIndexOfItsEnumValue()
    {
        // Arrange
        var all = PhaseCatalog.All;

        // Act
        var misplaced = all.Where((definition, index) => (int)definition.Phase != index).ToList();

        // Assert - journals and indicators index the catalogue by the enum value.
        Assert.Empty(misplaced);
    }

    [Fact]
    public void All_IndexOne_IsTheSimplifierDefinition()
    {
        // Arrange
        var all = PhaseCatalog.All;

        // Act
        var definition = all[1];

        // Assert
        Assert.Equal(WorkflowPhase.Simplifier, definition.Phase);
        Assert.Equal("Vereinfachen", definition.DisplayName);
        Assert.Equal("yo", definition.Launcher);
        Assert.Equal("simplifier_prompt.md", definition.PromptFile);
        Assert.Equal(CompletionRule.FilesExist, definition.Completion);
        Assert.Equal(PromptSubmissionMode.PositiveEvidence, definition.SubmissionMode);
    }

    [Theory]
    [InlineData(WorkflowPhase.Specification, "Spezifikation", "yo", "initial_prompt.md", CompletionRule.FilesExist)]
    [InlineData(WorkflowPhase.Simplifier, "Vereinfachen", "yo", "simplifier_prompt.md", CompletionRule.FilesExist)]
    [InlineData(WorkflowPhase.Review, "Review", @"& ""$env:LOCALAPPDATA\Programs\OpenAI\Codex\bin\codex.exe"" --yolo --no-daemon", "review_prompt.md", CompletionRule.FilesExist)]
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

    [Theory]
    [InlineData(WorkflowPhase.Specification, PromptSubmissionMode.PositiveEvidence)]
    [InlineData(WorkflowPhase.Simplifier, PromptSubmissionMode.PositiveEvidence)]
    [InlineData(WorkflowPhase.Review, PromptSubmissionMode.RetryUntilEvidence)]
    [InlineData(WorkflowPhase.ResolveReview, PromptSubmissionMode.PositiveEvidence)]
    [InlineData(WorkflowPhase.Implementation, PromptSubmissionMode.PositiveEvidence)]
    public void For_EachPhase_DeclaresHowItsPromptSubmissionIsVerified(
        WorkflowPhase phase, PromptSubmissionMode expected)
    {
        // Arrange
        var definition = PhaseCatalog.For(phase);

        // Act
        var mode = definition.SubmissionMode;

        // Assert - Claude Code (yo) submits once on positive evidence; Codex is retried until evidence.
        Assert.Equal(expected, mode);
    }
}
