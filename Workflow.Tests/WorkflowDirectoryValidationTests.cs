using System.IO;
using Workflow.Models;

namespace Workflow.Tests;

/// <remarks>
/// Requirement 1.3 and design "Components and Interfaces -&gt; Configuration and Persistence":
/// the tracking directory validator distinguishes four outcomes - valid, blank, missing, and
/// marker-absent - and each invalid outcome carries its own explanatory German message. The
/// message text itself is the contract here: the tab both gates the start action on
/// <see cref="WorkflowDirectoryValidation.IsValid"/> and presents
/// <see cref="WorkflowDirectoryValidation.ErrorMessage"/> at phase-4 entry (requirement 1.8), so a
/// flag-only assertion would not notice a validator that reported the wrong reason.
/// </remarks>
public sealed class WorkflowDirectoryValidationTests : IDisposable
{
    private const string BlankMessage = "Bitte ein Workflow-Verzeichnis auswählen.";

    private readonly string _root;

    public WorkflowDirectoryValidationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wf-dirval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string MissingMessage(string directory) =>
        $"Das Workflow-Verzeichnis '{directory}' existiert nicht.";

    private static string MarkerMessage(string directory) =>
        $"Das Verzeichnis '{directory}' enthält keinen Ordner 'task_template' und ist daher nicht das ausgecheckte Workflows-Repository.";

    private string CreateMarker() => Directory.CreateDirectory(
        Path.Combine(_root, SubtaskPaths.TemplateFolderName)).FullName;

    [Fact]
    public void Validate_AcceptsADirectoryThatContainsTheTemplateMarker()
    {
        CreateMarker();

        var result = WorkflowDirectoryValidation.Validate(_root);

        Assert.True(result.IsValid);
        Assert.Null(result.ErrorMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Validate_ReportsABlankSelectionWithItsOwnMessage(string? directory)
    {
        var result = WorkflowDirectoryValidation.Validate(directory);

        Assert.False(result.IsValid);
        Assert.Equal(BlankMessage, result.ErrorMessage);
    }

    [Fact]
    public void Validate_ReportsAMissingDirectoryWithItsOwnMessage()
    {
        var absent = Path.Combine(_root, "gibt-es-nicht");

        var result = WorkflowDirectoryValidation.Validate(absent);

        Assert.False(result.IsValid);
        Assert.Equal(MissingMessage(absent), result.ErrorMessage);
    }

    [Fact]
    public void Validate_ReportsAnExistingDirectoryWithoutTheMarkerWithItsOwnMessage()
    {
        Directory.CreateDirectory(Path.Combine(_root, "irgendein-ordner"));

        var result = WorkflowDirectoryValidation.Validate(_root);

        Assert.False(result.IsValid);
        Assert.Equal(MarkerMessage(_root), result.ErrorMessage);
    }

    /// <summary>
    /// The three invalid outcomes must be told apart by their text, not merely by the flag: a
    /// validator that answered "missing directory" for a blank selection would still be invalid.
    /// </summary>
    [Fact]
    public void Validate_GivesTheThreeInvalidCasesThreeDistinctMessages()
    {
        var absent = Path.Combine(_root, "gibt-es-nicht");

        var blank = WorkflowDirectoryValidation.Validate("   ").ErrorMessage;
        var missing = WorkflowDirectoryValidation.Validate(absent).ErrorMessage;
        var markerless = WorkflowDirectoryValidation.Validate(_root).ErrorMessage;

        Assert.Equal(BlankMessage, blank);
        Assert.Equal(MissingMessage(absent), missing);
        Assert.Equal(MarkerMessage(_root), markerless);
        Assert.All(new[] { blank, missing, markerless }, message => Assert.NotNull(message));
        Assert.NotEqual(blank, missing);
        Assert.NotEqual(blank, markerless);
        Assert.NotEqual(missing, markerless);
    }

    /// <summary>
    /// Requirement 1.3 explicitly forbids requiring a <c>.git</c> folder: the tracking repository is
    /// recognised by <c>task_template</c> alone, and users clone or copy it without git metadata.
    /// </summary>
    [Fact]
    public void Validate_AcceptsAMarkerBearingDirectoryThatHasNoGitFolder()
    {
        CreateMarker();
        Assert.False(Directory.Exists(Path.Combine(_root, ".git")));

        var result = WorkflowDirectoryValidation.Validate(_root);

        Assert.True(result.IsValid);
        Assert.Null(result.ErrorMessage);
    }

    /// <summary>The marker name is owned by <see cref="SubtaskPaths.TemplateFolderName"/>.</summary>
    [Fact]
    public void Validate_LooksForTheMarkerNameDeclaredBySubtaskPaths()
    {
        Directory.CreateDirectory(Path.Combine(_root, "task_template_backup"));
        Assert.False(WorkflowDirectoryValidation.Validate(_root).IsValid);

        Directory.CreateDirectory(Path.Combine(_root, SubtaskPaths.TemplateFolderName));

        Assert.True(WorkflowDirectoryValidation.Validate(_root).IsValid);
    }

    /// <summary>
    /// A <em>file</em> called <c>task_template</c> is not the template folder: the tracking contract
    /// reads <c>task_template/subtasks/...</c> beneath it, so accepting a file would let phase 4
    /// start against a directory whose template can never be read.
    /// </summary>
    [Fact]
    public void Validate_RejectsAMarkerThatIsAFileRatherThanAFolder()
    {
        File.WriteAllText(Path.Combine(_root, SubtaskPaths.TemplateFolderName), "nicht der Ordner");

        var result = WorkflowDirectoryValidation.Validate(_root);

        Assert.False(result.IsValid);
        Assert.Equal(MarkerMessage(_root), result.ErrorMessage);
    }

    [Fact]
    public void Validate_NormalisesTheSelectionBeforeCheckingIt()
    {
        CreateMarker();

        var result = WorkflowDirectoryValidation.Validate("  " + _root + Path.DirectorySeparatorChar + "  ");

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_ReportsTheNormalisedPathInItsMessage()
    {
        var absent = Path.Combine(_root, "gibt-es-nicht");

        var result = WorkflowDirectoryValidation.Validate(absent + Path.DirectorySeparatorChar);

        Assert.Equal(MissingMessage(absent), result.ErrorMessage);
    }

    [Fact]
    public void Ok_IsValidAndCarriesNoMessage()
    {
        Assert.True(WorkflowDirectoryValidation.Ok.IsValid);
        Assert.Null(WorkflowDirectoryValidation.Ok.ErrorMessage);
    }

    [Fact]
    public void Error_IsInvalidAndCarriesTheMessage()
    {
        var result = WorkflowDirectoryValidation.Error("Fehler.");

        Assert.False(result.IsValid);
        Assert.Equal("Fehler.", result.ErrorMessage);
    }
}
