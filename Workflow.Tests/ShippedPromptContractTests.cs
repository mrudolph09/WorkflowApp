using System.IO;
using System.Text.RegularExpressions;
using Workflow.Models;
using Workflow.Services;

namespace Workflow.Tests;

/// <summary>
/// Pins the file protocol the shipped subtask prompts must state, against the very files the build
/// copies next to the application (task 3.2, requirements 6.2, 6.4, 6.6 and 6.7).
/// </summary>
/// <remarks>
/// <para>
/// These tests read <c>Prompt\*.md</c> from the test output directory rather than a fixture, because
/// the defect they guard against is a shipped file disagreeing with <see cref="PromptVariables"/> and
/// <see cref="SubtaskPaths"/>. A fixture would agree with itself and prove nothing.
/// </para>
/// <para>
/// Separator-insensitive assertions compare against a copy in which every <c>\</c> has become
/// <c>/</c>. Both separators reach the same file on Windows, so a prompt's choice between them is
/// style, not contract; pinning one would make an editorial change fail a protocol test.
/// </para>
/// </remarks>
public sealed class ShippedPromptContractTests
{
    private const string CreateSubtasks = "create_subtasks.md";

    /// <summary>
    /// The token pattern of <see cref="PromptTemplateService"/>, restated here on purpose: a test that
    /// borrowed the production regex could not notice the production regex changing.
    /// </summary>
    private const string TokenPattern = @"\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}";

    // The same asymmetric fixture PromptVariablesTests uses: two different roots at two different
    // depths, and a task name that matches no folder in either, so a prompt that composes the wrong
    // root cannot render correctly by coincidence.
    private const string WorkingDirectory = @"C:\src\app";
    private const string TrackingRoot = @"D:\tracking\workflows\repo";
    private const string TaskName = "alpha-task";
    private const string TaskDescription = "Erste Zeile\nZweite Zeile";

    private static string ShippedPromptDirectory => Path.Combine(AppContext.BaseDirectory, "Prompt");

    private static string Read(string fileName) =>
        File.ReadAllText(Path.Combine(ShippedPromptDirectory, fileName));

    private static string ReadWithForwardSlashes(string fileName) =>
        Read(fileName).Replace('\\', '/');

    private static List<string> TokensIn(string fileName) =>
        Regex
            .Matches(Read(fileName), TokenPattern, RegexOptions.CultureInvariant)
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string RenderCreateSubtasks() =>
        new PromptTemplateService(ShippedPromptDirectory).Render(
            CreateSubtasks,
            PromptVariables.ForSubtaskCreation(
                new TaskPaths(WorkingDirectory, TaskName),
                TaskDescription,
                new SubtaskPaths(TrackingRoot, TaskName)));

    [Fact]
    public void CreateSubtasksPrompt_RendersWithoutAnUnknownPlaceholder()
    {
        // Render throws PromptTemplateException on the first token no substitution set covers, which
        // is what the shipped file did with {task_path} and {subtask_title}.
        var rendered = RenderCreateSubtasks();

        Assert.Contains(@"D:\tracking\workflows\repo\alpha-task", rendered, StringComparison.Ordinal);
        Assert.Contains(@"D:\tracking\workflows\repo\alpha-task\subtasks", rendered, StringComparison.Ordinal);
        Assert.Empty(Regex.Matches(rendered, TokenPattern, RegexOptions.CultureInvariant));
    }

    [Fact]
    public void CreateSubtasksPrompt_UsesOnlyTokensOfTheDecompositionSet()
    {
        var unsupported = TokensIn(CreateSubtasks)
            .Where(t => !PromptVariables.SubtaskCreationNames.Contains(t))
            .ToList();

        Assert.Empty(unsupported);
    }

    [Fact]
    public void CreateSubtasksPrompt_LeavesTheSubtaskTitleToTheAgent()
    {
        // The title is a name the session invents once per subtask. As a brace token the application
        // would substitute one value for all of them and destroy the instruction; the angle-bracket
        // form is invisible to the token regex.
        Assert.DoesNotContain("subtask_title", TokensIn(CreateSubtasks), StringComparer.Ordinal);
        Assert.Contains("<subtask_title>", Read(CreateSubtasks), StringComparison.Ordinal);
    }

    [Fact]
    public void CreateSubtasksPrompt_NamesTheTaskFolderAsTheIndexLocation()
    {
        var text = ReadWithForwardSlashes(CreateSubtasks);

        Assert.Contains("{task_path}/result.json", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{workflow_path}/result.json", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateSubtasksPrompt_DoesNotDeriveThePathsTheApplicationSupplies()
    {
        var text = ReadWithForwardSlashes(CreateSubtasks);

        // Both tokens are absolute (requirement 6.6), so prefixing either with the tracking root -
        // the defect in the source text - yields a doubled path.
        Assert.DoesNotContain("{workflow_path}/{tasktitel}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{workflow_path}/{task_path}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{workflow_path}/{subtask_path}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateSubtasksPrompt_UsesTheSingularFlagName()
    {
        var text = Read(CreateSubtasks);

        Assert.DoesNotContain("results.json", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("result.json", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateSubtasksPrompt_RequiresANonEmptyOrderedIndex()
    {
        var text = Read(CreateSubtasks);

        // A zero-byte file never satisfies the application's non-empty-file rule.
        Assert.Contains("darf nicht leer sein", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"subtasks\"", text, StringComparison.Ordinal);
        Assert.Contains("Ausführungsreihenfolge", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateSubtasksPrompt_ShowsPendingAsTheInitialStatus()
    {
        var text = Read(CreateSubtasks);

        Assert.Contains("\"status\": \"pending\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"status\": \"complete\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateSubtasksPrompt_RequiresAtomicPublicationOfBothFiles()
    {
        var text = Read(CreateSubtasks);

        Assert.Contains("status.json.tmp", text, StringComparison.Ordinal);
        Assert.Contains("result.json.tmp", text, StringComparison.Ordinal);
        Assert.Contains("umbenennen", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateSubtasksPrompt_ExplainsWhatMakesASubtaskTitleUnsafe()
    {
        var text = Read(CreateSubtasks);

        // The rules the application enforces in SubtaskPaths.IsValidTitle, stated where the titles
        // are invented rather than discovered when the index is rejected.
        Assert.Contains("Ordnername", text, StringComparison.Ordinal);
        Assert.Contains("`\\`", text, StringComparison.Ordinal);
        Assert.Contains("`/`", text, StringComparison.Ordinal);
        Assert.Contains("`:`", text, StringComparison.Ordinal);
        Assert.Contains("`..`", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateSubtasksPrompt_TitlesTheApplicationRejectsAreTheOnesItCallsUnsafe()
    {
        // Pins the explanation to the rule rather than to its wording: every character the prompt
        // forbids really is rejected, so the two cannot drift apart.
        Assert.False(SubtaskPaths.IsValidTitle(@"ST-001\nested"));
        Assert.False(SubtaskPaths.IsValidTitle("ST-001/nested"));
        Assert.False(SubtaskPaths.IsValidTitle("C:ST-001"));
        Assert.False(SubtaskPaths.IsValidTitle(".."));
        Assert.True(SubtaskPaths.IsValidTitle("ST-001-kurzer-titel"));
    }
}
