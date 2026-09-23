using System.IO;
using System.Text.RegularExpressions;
using Workflow.Models;
using Workflow.Services;

namespace Workflow.Tests;

/// <summary>
/// Pins the file protocol the shipped subtask prompts must state, against the very files the build
/// copies next to the application (tasks 3.2 and 3.3, requirements 6.2, 6.4, 6.6, 6.7 and 6.8).
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
    private const string RunSubtask = "run_subtask.md";

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

    // Execution adds two more dimensions, kept distinct from every value above and from each other so
    // a prompt that substituted the wrong one would render visibly wrong rather than plausibly right.
    private const string SubtaskTitle = "ST-042-flag-order";
    private const string SubtaskBody = "Koerper der Subtask-Beschreibung";

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

    private static string RenderRunSubtask() =>
        new PromptTemplateService(ShippedPromptDirectory).Render(
            RunSubtask,
            PromptVariables.ForSubtaskRun(
                new TaskPaths(WorkingDirectory, TaskName),
                TaskDescription,
                new SubtaskPaths(TrackingRoot, TaskName),
                SubtaskTitle,
                SubtaskBody));

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

    [Fact]
    public void RunSubtaskPrompt_RendersWithoutAnUnknownPlaceholder()
    {
        var rendered = RenderRunSubtask();

        // The substituted values carry Windows separators while the prompt writes its own with '/',
        // so the composed paths are compared in the same separator-insensitive form as the sources.
        var slashed = rendered.Replace('\\', '/');

        Assert.Contains(SubtaskBody, rendered, StringComparison.Ordinal);
        Assert.Contains(
            "D:/tracking/workflows/repo/alpha-task/subtasks/ST-042-flag-order",
            slashed,
            StringComparison.Ordinal);

        // The doubled prefix the source text produced: an absolute token pasted behind another root.
        Assert.DoesNotContain(
            "D:/tracking/workflows/repo/D:/tracking/workflows/repo",
            slashed,
            StringComparison.Ordinal);
        Assert.Empty(Regex.Matches(rendered, TokenPattern, RegexOptions.CultureInvariant));
    }

    [Fact]
    public void RunSubtaskPrompt_UsesOnlyTokensOfTheExecutionSet()
    {
        var unsupported = TokensIn(RunSubtask)
            .Where(t => !PromptVariables.SubtaskRunNames.Contains(t))
            .ToList();

        Assert.Empty(unsupported);
    }

    [Fact]
    public void RunSubtaskPrompt_LetsTheApplicationSubstituteTheSubtaskTitle()
    {
        // The inverse of CreateSubtasksPrompt_LeavesTheSubtaskTitleToTheAgent: at execution time the
        // application is running one known subtask and holds its title, so re-deriving it in angle
        // form would hand the session a name it cannot resolve (requirements 6.4 and 6.6).
        Assert.Contains("subtask_title", TokensIn(RunSubtask), StringComparer.Ordinal);
        Assert.DoesNotContain("<subtask_title>", Read(RunSubtask), StringComparison.Ordinal);
    }

    [Fact]
    public void RunSubtaskPrompt_DoesNotDeriveThePathsTheApplicationSupplies()
    {
        var text = ReadWithForwardSlashes(RunSubtask);

        Assert.DoesNotContain("{workflow_path}/{subtask_path}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{workflow_path}/{task_path}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{task_path}/subtasks", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RunSubtaskPrompt_NamesTheTaskFolderForGlobalFindings()
    {
        var text = ReadWithForwardSlashes(RunSubtask);

        // Requirement 6.8: task-wide findings live beside the per-subtask ones, at the task root, so
        // that {subtask_path} holds nothing but subtask folders.
        Assert.Contains("{task_path}/findings.md", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{subtask_path}/findings.md", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RunSubtaskPrompt_NamesBothCompletionFilesInTheSubtaskFolder()
    {
        var text = ReadWithForwardSlashes(RunSubtask);

        Assert.Contains("{subtask_path}/{subtask_title}/status.json", text, StringComparison.Ordinal);
        Assert.Contains("{subtask_path}/{subtask_title}/result.json", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RunSubtaskPrompt_UsesTheSingularFlagName()
    {
        var text = Read(RunSubtask);

        Assert.DoesNotContain("results.json", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("result.json", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RunSubtaskPrompt_RequiresANonEmptyFlag()
    {
        var text = Read(RunSubtask);

        // A zero-byte file reads as no flag at all, so the session would appear never to have finished.
        Assert.Contains("must not be empty", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0 byte", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RunSubtaskPrompt_RequiresAtomicPublicationOfBothFiles()
    {
        var text = Read(RunSubtask);

        Assert.Contains("status.json.tmp", text, StringComparison.Ordinal);
        Assert.Contains("result.json.tmp", text, StringComparison.Ordinal);
        Assert.Contains("rename", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RunSubtaskPrompt_PublishesTheStatusPayloadBeforeTheFlag()
    {
        var text = Read(RunSubtask);

        // Structural, not editorial: whatever the wording, the temporary file of the payload must be
        // introduced before the temporary file of the flag the application actually watches (6.2).
        var status = text.IndexOf("status.json.tmp", StringComparison.Ordinal);
        var flag = text.IndexOf("result.json.tmp", StringComparison.Ordinal);

        Assert.InRange(status, 0, int.MaxValue);
        Assert.InRange(flag, status + 1, int.MaxValue);
    }

    [Fact]
    public void RunSubtaskPrompt_StatesThePublicationOrderBeforeItDescribesEitherFile()
    {
        var text = Read(RunSubtask);

        // The sibling test above compares the two *.tmp names, which the prompt happens to list in one
        // single sentence of the atomic-publication section; that pins word order inside a list, not
        // the protocol. Requirement 6.2 is about which file reaches the disk first, so this compares
        // the bare names: their first mention is the explicit order statement, and inverting it - the
        // exact defect the application cannot survive, because it watches the flag and reads the
        // payload only afterwards - moves the flag in front of the payload here.
        var status = text.IndexOf("status.json", StringComparison.Ordinal);
        var flag = text.IndexOf("result.json", StringComparison.Ordinal);

        Assert.InRange(status, 0, int.MaxValue);
        Assert.InRange(flag, status + 1, int.MaxValue);
    }

    [Fact]
    public void RunSubtaskPrompt_StatesTheOutcomeFieldsTheApplicationReads()
    {
        var text = Read(RunSubtask);

        Assert.Contains("\"status\": \"complete\"", text, StringComparison.Ordinal);
        Assert.Contains("failreason", text, StringComparison.Ordinal);
        Assert.Contains("\"failed\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RunSubtaskPrompt_DoesNotAskForAnOrderedIndexInTheFlag()
    {
        var text = Read(RunSubtask);

        // The ordered index is the creation prompt's task-level result.json (requirement 6.7). The
        // per-subtask flag is only a flag; the application never interprets its contents.
        Assert.DoesNotContain("right order", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"subtasks\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RunSubtaskPrompt_KeepsTheAcceptanceGate()
    {
        // Design P12: correcting the file protocol must not cost the verification gate.
        Assert.Contains("verify.ps1", Read(RunSubtask), StringComparison.Ordinal);
    }
}
