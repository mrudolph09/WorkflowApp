using System.IO;
using Workflow.Models;
using Workflow.Services;

namespace Workflow.Tests;

/// <summary>
/// Pins the layered substitution sets of <see cref="PromptVariables"/> (task 3.1, requirements 6.4
/// and 6.6). Two properties are load-bearing for the prompt tasks that follow: every new
/// path-bearing token is absolute, and the four existing phase prompts render exactly as before.
/// </summary>
public sealed class PromptVariablesTests
{
    // Deliberately asymmetric fixture: the working directory and the tracking root are different
    // volumes with different depths, and the task name differs from every folder name in either
    // path, so a builder that confuses the two roots cannot pass by coincidence.
    private const string WorkingDirectory = @"C:\src\app";
    private const string TrackingRoot = @"D:\tracking\workflows\repo";
    private const string TaskName = "alpha-task";
    private const string TaskDescription = "Erste Zeile\nZweite Zeile";
    private const string SubtaskTitle = "ST-002-beta";
    private const string SubtaskBody = "# ST-002-beta\n\nBeschreibung des Subtasks.";

    private static readonly string[] BaseTokenNames =
    [
        "taskbezeichnung",
        "taskbeschreibung",
        "AppDirectory",
        "spec_path",
        "plan_path",
        "review_path",
        "done_path",
    ];

    private static readonly string[] CreationTokenNames =
    [
        "AppDirectory",
        "done_path",
        "plan_path",
        "review_path",
        "spec_path",
        "subtask_path",
        "task_path",
        "taskbeschreibung",
        "taskbezeichnung",
        "tasktitel",
        "workflow_path",
    ];

    private static readonly string[] RunTokenNames =
    [
        "AppDirectory",
        "done_path",
        "plan_path",
        "review_path",
        "spec_path",
        "subtask",
        "subtask_path",
        "subtask_title",
        "task_path",
        "taskbeschreibung",
        "taskbezeichnung",
        "tasktitel",
        "workflow_path",
    ];

    /// <summary>The four phase prompts whose rendering must not change.</summary>
    public static TheoryData<string> ExistingPhasePrompts() => new(
        "initial_prompt.md",
        "review_prompt.md",
        "resolve_review_prompt.md",
        "implementation_prompt.md");

    private static TaskPaths Paths() => new(WorkingDirectory, TaskName);

    private static SubtaskPaths Tracking() => new(TrackingRoot, TaskName);

    private static string[] Sorted(IEnumerable<string> names) =>
        names.OrderBy(n => n, StringComparer.Ordinal).ToArray();

    // --- Layer 1: the existing base set must stay byte-identical. ---------------------------

    [Fact]
    public void KnownNames_StillHoldsExactlyTheSevenBaseTokens()
    {
        Assert.Equal(Sorted(BaseTokenNames), Sorted(PromptVariables.KnownNames));
    }

    [Fact]
    public void For_StillProducesTheExactBaseValues()
    {
        var variables = PromptVariables.For(Paths(), TaskDescription);

        Assert.Equal(Sorted(BaseTokenNames), Sorted(variables.Keys));
        Assert.Equal(TaskName, variables["taskbezeichnung"]);
        Assert.Equal(TaskDescription, variables["taskbeschreibung"]);
        Assert.Equal(WorkingDirectory, variables["AppDirectory"]);
        Assert.Equal("./alpha-task/alpha-task_spec.md", variables["spec_path"]);
        Assert.Equal("./alpha-task/alpha-task_plan.md", variables["plan_path"]);
        Assert.Equal("./alpha-task/alpha-task-review.md", variables["review_path"]);
        Assert.Equal("./alpha-task/alpha-task-done.md", variables["done_path"]);
    }

    // --- Layer 2: decomposition extends the base set. ----------------------------------------

    [Fact]
    public void SubtaskCreationNames_ExtendTheBaseSetWithTheFourTrackingTokens()
    {
        Assert.Equal(Sorted(CreationTokenNames), Sorted(PromptVariables.SubtaskCreationNames));
        Assert.All(PromptVariables.KnownNames, name => Assert.Contains(name, PromptVariables.SubtaskCreationNames));
    }

    [Fact]
    public void ForSubtaskCreation_KeepsEveryBaseEntryUnchanged()
    {
        var baseline = PromptVariables.For(Paths(), TaskDescription);

        var variables = PromptVariables.ForSubtaskCreation(Paths(), TaskDescription, Tracking());

        foreach (var pair in baseline)
        {
            Assert.True(variables.TryGetValue(pair.Key, out var value), $"Token {pair.Key} fehlt.");
            Assert.Equal(pair.Value, value);
        }
    }

    [Fact]
    public void ForSubtaskCreation_AddsTheAbsoluteTrackingRootTaskAndSubtasksFolders()
    {
        var variables = PromptVariables.ForSubtaskCreation(Paths(), TaskDescription, Tracking());

        Assert.Equal(TrackingRoot, variables["workflow_path"]);
        Assert.Equal(Path.Combine(TrackingRoot, TaskName), variables["task_path"]);
        Assert.Equal(Path.Combine(TrackingRoot, TaskName, "subtasks"), variables["subtask_path"]);
    }

    [Fact]
    public void ForSubtaskCreation_SubstitutesTheTrackingPathsAsAbsolutePaths()
    {
        var variables = PromptVariables.ForSubtaskCreation(Paths(), TaskDescription, Tracking());

        foreach (var name in new[] { "workflow_path", "task_path", "subtask_path" })
        {
            Assert.True(
                Path.IsPathFullyQualified(variables[name]),
                $"Der Token {{{name}}} muss einen absoluten Pfad liefern, war aber '{variables[name]}'.");
        }
    }

    [Fact]
    public void ForSubtaskCreation_SubtasksFolderSitsInsideTheTaskFolder()
    {
        var variables = PromptVariables.ForSubtaskCreation(Paths(), TaskDescription, Tracking());

        Assert.StartsWith(variables["task_path"], variables["subtask_path"], StringComparison.Ordinal);
        Assert.NotEqual(variables["task_path"], variables["subtask_path"]);
    }

    [Fact]
    public void ForSubtaskCreation_AliasesTheTaskTitleWithoutRemovingTheTaskName()
    {
        var variables = PromptVariables.ForSubtaskCreation(Paths(), TaskDescription, Tracking());

        Assert.Equal(TaskName, variables["tasktitel"]);
        Assert.Equal(TaskName, variables["taskbezeichnung"]);
        Assert.Contains("taskbezeichnung", PromptVariables.SubtaskCreationNames);
        Assert.Contains("tasktitel", PromptVariables.SubtaskCreationNames);
    }

    [Fact]
    public void ForSubtaskCreation_CoversItsOwnNameSetExactly()
    {
        var variables = PromptVariables.ForSubtaskCreation(Paths(), TaskDescription, Tracking());

        Assert.Equal(Sorted(PromptVariables.SubtaskCreationNames), Sorted(variables.Keys));
    }

    [Fact]
    public void ForSubtaskCreation_RejectsAMissingTrackingPathSet()
    {
        Assert.Throws<ArgumentNullException>(
            () => PromptVariables.ForSubtaskCreation(Paths(), TaskDescription, null!));
    }

    // --- Layer 3: execution extends the decomposition set. -----------------------------------

    [Fact]
    public void SubtaskRunNames_ExtendTheCreationSetWithTheTitleAndBodyTokens()
    {
        Assert.Equal(Sorted(RunTokenNames), Sorted(PromptVariables.SubtaskRunNames));
        Assert.All(PromptVariables.SubtaskCreationNames, name => Assert.Contains(name, PromptVariables.SubtaskRunNames));
        Assert.Equal(PromptVariables.SubtaskCreationNames.Count + 2, PromptVariables.SubtaskRunNames.Count);
    }

    [Fact]
    public void ForSubtaskRun_KeepsEveryDecompositionEntryUnchanged()
    {
        var creation = PromptVariables.ForSubtaskCreation(Paths(), TaskDescription, Tracking());

        var variables = PromptVariables.ForSubtaskRun(
            Paths(), TaskDescription, Tracking(), SubtaskTitle, SubtaskBody);

        foreach (var pair in creation)
        {
            Assert.True(variables.TryGetValue(pair.Key, out var value), $"Token {pair.Key} fehlt.");
            Assert.Equal(pair.Value, value);
        }

        Assert.Equal(creation.Count + 2, variables.Count);
    }

    [Fact]
    public void ForSubtaskRun_AddsThePerSubtaskTitleAndBody()
    {
        var variables = PromptVariables.ForSubtaskRun(
            Paths(), TaskDescription, Tracking(), SubtaskTitle, SubtaskBody);

        Assert.Equal(SubtaskTitle, variables["subtask_title"]);
        Assert.Equal(SubtaskBody, variables["subtask"]);
    }

    [Fact]
    public void ForSubtaskRun_PreservesAMultiLineSubtaskBody()
    {
        var variables = PromptVariables.ForSubtaskRun(
            Paths(), TaskDescription, Tracking(), SubtaskTitle, "Zeile eins\nZeile zwei\n");

        Assert.Equal("Zeile eins\nZeile zwei\n", variables["subtask"]);
    }

    [Fact]
    public void ForSubtaskRun_CoversItsOwnNameSetExactly()
    {
        var variables = PromptVariables.ForSubtaskRun(
            Paths(), TaskDescription, Tracking(), SubtaskTitle, SubtaskBody);

        Assert.Equal(Sorted(PromptVariables.SubtaskRunNames), Sorted(variables.Keys));
    }

    [Fact]
    public void ForSubtaskRun_RejectsABlankSubtaskTitle()
    {
        Assert.Throws<ArgumentException>(
            () => PromptVariables.ForSubtaskRun(Paths(), TaskDescription, Tracking(), "  ", SubtaskBody));
    }

    // --- The regression pin: the four existing phase prompts render exactly as before. -------

    /// <summary>
    /// Substitutes the base tokens independently of <see cref="PromptVariables"/>, from literal
    /// names and literal values. If a base token is renamed, removed or given a different value,
    /// the rendered text stops matching this expectation.
    /// </summary>
    private static string SubstituteIndependently(string template)
    {
        var literals = new (string Name, string Value)[]
        {
            ("taskbezeichnung", "alpha-task"),
            ("taskbeschreibung", "Erste Zeile\nZweite Zeile"),
            ("AppDirectory", @"C:\src\app"),
            ("spec_path", "./alpha-task/alpha-task_spec.md"),
            ("plan_path", "./alpha-task/alpha-task_plan.md"),
            ("review_path", "./alpha-task/alpha-task-review.md"),
            ("done_path", "./alpha-task/alpha-task-done.md"),
        };

        var text = template;
        foreach (var (name, value) in literals)
        {
            text = text.Replace("{" + name + "}", value, StringComparison.Ordinal);
        }

        return text;
    }

    private static string ShippedPromptDirectory => Path.Combine(AppContext.BaseDirectory, "Prompt");

    [Theory]
    [MemberData(nameof(ExistingPhasePrompts))]
    public void ExistingPhasePrompt_RendersExactlyAsBefore(string fileName)
    {
        var raw = File.ReadAllText(Path.Combine(ShippedPromptDirectory, fileName));
        var service = new PromptTemplateService(ShippedPromptDirectory);

        var rendered = service.Render(fileName, PromptVariables.For(Paths(), TaskDescription));

        Assert.Equal(SubstituteIndependently(raw), rendered);
        Assert.NotEqual(raw, rendered);
    }

    [Theory]
    [MemberData(nameof(ExistingPhasePrompts))]
    public void ExistingPhasePrompt_RendersIdenticallyWithTheExtendedSets(string fileName)
    {
        var service = new PromptTemplateService(ShippedPromptDirectory);
        var expected = service.Render(fileName, PromptVariables.For(Paths(), TaskDescription));

        var withCreation = service.Render(
            fileName, PromptVariables.ForSubtaskCreation(Paths(), TaskDescription, Tracking()));
        var withRun = service.Render(
            fileName,
            PromptVariables.ForSubtaskRun(Paths(), TaskDescription, Tracking(), SubtaskTitle, SubtaskBody));

        Assert.Equal(expected, withCreation);
        Assert.Equal(expected, withRun);
    }

    [Fact]
    public void ValidateAll_StillAcceptsTheShippedPhaseTemplates()
    {
        var service = new PromptTemplateService(ShippedPromptDirectory);

        Assert.Empty(service.ValidateAll());
    }
}
