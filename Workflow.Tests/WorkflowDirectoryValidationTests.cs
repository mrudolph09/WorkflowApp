using System.IO;
using System.Text;
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

    /// <summary>
    /// The three messages are pinned above against literals that live in this same source tree and
    /// therefore share its encoding. If the validator's file and this file were ever re-saved in a
    /// non-UTF-8 encoding <em>together</em>, both sides would be wrong in exactly the same way,
    /// every equality assertion above would still pass, and the product would ship mojibake. This
    /// is the encoding-proof pin (method carried forward from task 6.1): the expected fragments are
    /// written as ASCII-only C# unicode escapes, so no re-encoding of any source file can corrupt
    /// them, and they are looked for in the compiled assembly rather than in source. Nothing in
    /// this test, comments included, is allowed to be a non-ASCII byte.
    /// </summary>
    /// <remarks>
    /// The encoding is chosen by where the string lives. A C# string literal is stored in the
    /// <c>#US</c> metadata heap as UTF-16LE; only a XAML literal reaches the assembly as UTF-8,
    /// inside compiled BAML, which is why <c>PhaseCompletionRemovalTests</c> scans for UTF-8 and
    /// this test must not.
    /// </remarks>
    [Fact]
    public void TheCompiledApplication_ShipsTheThreeMessagesWithTheirGermanCharactersIntact()
    {
        var assembly = File.ReadAllBytes(typeof(WorkflowDirectoryValidation).Assembly.Location);

        // Positive control of the same storage class as the two cases below: a fragment of the
        // missing-directory message, the one message that carries no German character at all. If
        // the #US heap scan itself stopped working, this fails first.
        Assert.True(
            CarriesUtf16(assembly, "Das Workflow-Verzeichnis '"),
            "The #US heap scan found no part of the missing-directory message.");

        // Each fragment below is asserted to occur in the assembly, so it must identify exactly
        // one message. The bare fragment "auswaehlen." does not: the unrelated
        // TaskFolderService message "Bitte ein gueltiges Arbeitsverzeichnis auswaehlen." carries
        // the same tail, and would keep this assertion green while the blank-selection message
        // rotted. The full message text is used instead, which occurs once.
        Assert.True(
            CarriesUtf16(assembly, "Bitte ein Workflow-Verzeichnis ausw\u00e4hlen."),
            "The shipped blank-selection message is no longer the exact German text that spells "
                + "'auswaehlen' with an a-umlaut.");
        Assert.True(
            CarriesUtf16(assembly, "enth\u00e4lt keinen Ordner "),
            "The shipped marker message no longer spells 'enthaelt' with an a-umlaut.");

        // The signature of a UTF-8 file re-read as Windows-1252: a-umlaut becomes 'A-tilde' plus a
        // currency-sign character. It is what the user would see, and exactly what an equality
        // assertion against a co-corrupted literal is structurally unable to see.
        Assert.False(
            CarriesUtf16(assembly, "Bitte ein Workflow-Verzeichnis ausw\u00c3\u00a4hlen."),
            "The shipped assembly carries the mojibake spelling of the blank-selection message.");
    }

    private static bool CarriesUtf16(byte[] assembly, string literal) =>
        assembly.AsSpan().IndexOf(Encoding.Unicode.GetBytes(literal).AsSpan()) >= 0;
}
