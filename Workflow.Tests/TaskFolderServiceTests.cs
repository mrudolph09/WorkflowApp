using System.IO;
using Workflow.Models;
using Workflow.Services;

namespace Workflow.Tests;

public sealed class TaskFolderServiceTests : IDisposable
{
    private readonly string _root;
    private readonly TaskFolderService _service = new();

    public TaskFolderServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wf-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // Regression: a drive root must stay a root. WorkingDirectoryPath.Normalise keeps "C:\";
    // a plain TrimEnd would make the combined path "C:name" and mis-measure the length budget.
    [Fact]
    public void Validate_AcceptsANameUnderADriveRoot()
    {
        var result = _service.Validate("wf-root-test", @"C:\");

        Assert.True(result.IsValid, result.ErrorMessage);
    }

    [Fact]
    public void Rename_ComposesPathsUnderADriveRootWithoutGoingDriveRelative()
    {
        // Exercised through TaskPaths so no folder is created on C:\ by the test run.
        var paths = new TaskPaths(@"C:\", "wf-root-test");

        Assert.Equal(@"C:\wf-root-test", paths.TaskDirectory);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RejectsEmptyName(string? name)
    {
        Assert.False(_service.Validate(name, _root).IsValid);
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a\"b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a|b")]
    public void Validate_RejectsInvalidFileNameCharacters(string name)
    {
        Assert.False(_service.Validate(name, _root).IsValid);
    }

    [Theory]
    [InlineData("task.")]
    [InlineData("task ")]
    public void Validate_RejectsTrailingDotOrSpace(string name)
    {
        Assert.False(_service.Validate(name, _root).IsValid);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    [InlineData("lpt3.txt")]
    public void Validate_RejectsReservedDeviceNames(string name)
    {
        Assert.False(_service.Validate(name, _root).IsValid);
    }

    [Fact]
    public void Validate_RejectsWhenCombinedPathExceeds240Characters()
    {
        var name = new string('x', 240);

        Assert.False(_service.Validate(name, _root).IsValid);
    }

    [Theory]
    [InlineData("my-task")]
    [InlineData("Größe prüfen")]
    [InlineData("task 1")]
    [InlineData("COM0")]
    [InlineData("CONSOLE")]
    public void Validate_AcceptsUsableNames(string name)
    {
        var result = _service.Validate(name, _root);

        Assert.True(result.IsValid, result.ErrorMessage);
    }

    [Fact]
    public void Validate_RejectsMissingWorkingDirectory()
    {
        Assert.False(_service.Validate("my-task", null).IsValid);
        Assert.False(_service.Validate("my-task", Path.Combine(_root, "does-not-exist")).IsValid);
    }

    [Fact]
    public void EnsureCreated_CreatesTheFolder()
    {
        var paths = new TaskPaths(_root, "my-task");

        _service.EnsureCreated(paths);

        Assert.True(Directory.Exists(paths.TaskDirectory));
    }

    [Fact]
    public void DirectoryAlreadyExisted_IsTrueOnlyWhenTheFolderIsThereBeforehand()
    {
        var paths = new TaskPaths(_root, "my-task");

        Assert.False(_service.DirectoryAlreadyExisted(paths));

        _service.EnsureCreated(paths);

        Assert.True(_service.DirectoryAlreadyExisted(paths));
    }

    [Fact]
    public void EnsureCreated_IsIdempotentAndKeepsExistingContent()
    {
        var paths = new TaskPaths(_root, "my-task");
        _service.EnsureCreated(paths);
        File.WriteAllText(Path.Combine(paths.TaskDirectory, "keep.txt"), "keep");

        _service.EnsureCreated(paths);

        Assert.True(File.Exists(Path.Combine(paths.TaskDirectory, "keep.txt")));
    }

    [Fact]
    public void Rename_MovesTheFolder()
    {
        _service.EnsureCreated(new TaskPaths(_root, "old"));

        _service.Rename(_root, "old", "new");

        Assert.False(Directory.Exists(Path.Combine(_root, "old")));
        Assert.True(Directory.Exists(Path.Combine(_root, "new")));
    }

    [Fact]
    public void Rename_HandlesCaseOnlyChange()
    {
        _service.EnsureCreated(new TaskPaths(_root, "task"));

        _service.Rename(_root, "task", "Task");

        var actual = Path.GetFileName(Directory.GetDirectories(_root).Single());
        Assert.Equal("Task", actual);
    }

    [Fact]
    public void Rename_IsANoOpWhenTheSourceDoesNotExist()
    {
        _service.Rename(_root, "missing", "other");

        Assert.Empty(Directory.GetDirectories(_root));
    }

    // --- Workflow_LoadFix spec section 6: adopting a misnamed spec and plan -------------------

    private TaskPaths NewTask(string name)
    {
        var paths = new TaskPaths(_root, name);
        Directory.CreateDirectory(paths.TaskDirectory);
        return paths;
    }

    [Fact]
    public void FindMissingPlanningArtefacts_BothMissingWithCandidates_ListsSpecThenPlan()
    {
        // Arrange - the VSDM2 shape.
        var paths = NewTask("VSDM2");
        File.WriteAllText(Path.Combine(paths.TaskDirectory, "2026-09-30-vsdm2-popp-zeta-design.md"), "spec");
        File.WriteAllText(Path.Combine(paths.TaskDirectory, "2026-09-30-vsdm2-popp-zeta.md"), "plan");

        // Act
        var missing = _service.FindMissingPlanningArtefacts(paths);

        // Assert
        Assert.Equal(new[] { paths.SpecAbsolute, paths.PlanAbsolute }, missing);
    }

    [Fact]
    public void FindMissingPlanningArtefacts_OnlyThePlanMissing_ListsOnlyThePlan()
    {
        // Arrange
        var paths = NewTask("alpha");
        File.WriteAllText(paths.SpecAbsolute, "spec");
        File.WriteAllText(Path.Combine(paths.TaskDirectory, "notes.md"), "plan");

        // Act
        var missing = _service.FindMissingPlanningArtefacts(paths);

        // Assert
        Assert.Equal(new[] { paths.PlanAbsolute }, missing);
    }

    [Fact]
    public void FindMissingPlanningArtefacts_BothPresent_ListsNothing()
    {
        // Arrange
        var paths = NewTask("alpha");
        File.WriteAllText(paths.SpecAbsolute, "spec");
        File.WriteAllText(paths.PlanAbsolute, "plan");
        File.WriteAllText(Path.Combine(paths.TaskDirectory, "notes.md"), "x");

        // Act
        var missing = _service.FindMissingPlanningArtefacts(paths);

        // Assert
        Assert.Empty(missing);
    }

    [Fact]
    public void FindMissingPlanningArtefacts_NoCandidate_ListsNothing()
    {
        // Arrange - only canonical artefacts and a non-Markdown file: nothing could be adopted.
        var paths = NewTask("alpha");
        File.WriteAllText(paths.ReviewAbsolute, "review");
        File.WriteAllText(paths.DoneAbsolute, "done");
        File.WriteAllText(Path.Combine(paths.TaskDirectory, "notes.txt"), "x");

        // Act
        var missing = _service.FindMissingPlanningArtefacts(paths);

        // Assert
        Assert.Empty(missing);
    }

    [Fact]
    public void FindMissingPlanningArtefacts_FolderMissing_ListsNothing()
    {
        // Arrange
        var paths = new TaskPaths(_root, "gibt-es-nicht");

        // Act
        var missing = _service.FindMissingPlanningArtefacts(paths);

        // Assert
        Assert.Empty(missing);
    }

    [Fact]
    public void FindMissingPlanningArtefacts_ZeroByteSpecNextToACandidate_ListsTheSpec()
    {
        // Arrange - a 0-byte spec is absent evidence for the reconciliation, so it is missing here
        // too (Workflow_LoadFix spec 6.2, F25).
        var paths = NewTask("alpha");
        File.WriteAllBytes(paths.SpecAbsolute, []);
        File.WriteAllText(paths.PlanAbsolute, "plan");
        File.WriteAllText(Path.Combine(paths.TaskDirectory, "notes.md"), "spec");

        // Act
        var missing = _service.FindMissingPlanningArtefacts(paths);

        // Assert
        Assert.Equal(new[] { paths.SpecAbsolute }, missing);
    }

    [Fact]
    public void FindMissingPlanningArtefacts_ZeroByteFilesWithoutACandidate_ListsNothing()
    {
        // Arrange - empty canonical files are still canonical, so they are not candidates.
        var paths = NewTask("alpha");
        File.WriteAllBytes(paths.SpecAbsolute, []);
        File.WriteAllBytes(paths.PlanAbsolute, []);

        // Act
        var missing = _service.FindMissingPlanningArtefacts(paths);

        // Assert
        Assert.Empty(missing);
    }

    [Fact]
    public void AdoptArtefact_MovesTheFileAndKeepsItsContentAndWriteTime()
    {
        // Arrange
        var paths = NewTask("alpha");
        var source = Path.Combine(paths.TaskDirectory, "entwurf-design.md");
        File.WriteAllText(source, "die Spezifikation");
        var written = new DateTime(2026, 10, 5, 15, 25, 17, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, written);

        // Act
        _service.AdoptArtefact(source, paths.SpecAbsolute);

        // Assert - the write time is what promotion dates phase 1 from.
        Assert.False(File.Exists(source));
        Assert.Equal("die Spezifikation", File.ReadAllText(paths.SpecAbsolute));
        Assert.Equal(written, File.GetLastWriteTimeUtc(paths.SpecAbsolute));
    }

    [Fact]
    public void AdoptArtefact_TargetExists_ThrowsAndChangesNothing()
    {
        // Arrange
        var paths = NewTask("alpha");
        var source = Path.Combine(paths.TaskDirectory, "entwurf.md");
        File.WriteAllText(source, "neu");
        File.WriteAllText(paths.PlanAbsolute, "alt");

        // Act
        var error = Record.Exception(() => _service.AdoptArtefact(source, paths.PlanAbsolute));

        // Assert
        Assert.IsAssignableFrom<IOException>(error);
        Assert.Equal("neu", File.ReadAllText(source));
        Assert.Equal("alt", File.ReadAllText(paths.PlanAbsolute));
    }

    [Fact]
    public void AdoptArtefact_NamesWithSpacesAndUmlauts_AreMoved()
    {
        // Arrange - Review Focus 1: German task and file names.
        var paths = NewTask("Feature Ü 1");
        var source = Path.Combine(paths.TaskDirectory, "Entwurf Änderung.md");
        File.WriteAllText(source, "Plan");

        // Act
        var missing = _service.FindMissingPlanningArtefacts(paths);
        _service.AdoptArtefact(source, paths.PlanAbsolute);

        // Assert
        Assert.Equal(new[] { paths.SpecAbsolute, paths.PlanAbsolute }, missing);
        Assert.Equal(Path.Combine(paths.TaskDirectory, "Feature Ü 1_plan.md"), paths.PlanAbsolute);
        Assert.Equal("Plan", File.ReadAllText(paths.PlanAbsolute));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public void AdoptArtefact_ZeroByteTarget_IsReplacedByTheChosenFile()
    {
        // Arrange - a 0-byte artefact holds nothing to lose and counts as missing (spec 6.3, D17).
        var paths = NewTask("alpha");
        File.WriteAllBytes(paths.SpecAbsolute, []);
        var source = Path.Combine(paths.TaskDirectory, "entwurf-design.md");
        File.WriteAllText(source, "die Spezifikation");

        // Act
        _service.AdoptArtefact(source, paths.SpecAbsolute);

        // Assert
        Assert.False(File.Exists(source));
        Assert.Equal("die Spezifikation", File.ReadAllText(paths.SpecAbsolute));
    }

    [Fact]
    public void AdoptArtefact_SourceInAnotherFolder_IsMovedIntoTheTaskFolder()
    {
        // Arrange - Review Focus 2: the chosen file may lie outside the task folder.
        var paths = NewTask("alpha");
        var docs = Path.Combine(_root, "docs", "superpowers", "plans");
        Directory.CreateDirectory(docs);
        var source = Path.Combine(docs, "2026-10-05-alpha.md");
        File.WriteAllText(source, "Plan");

        // Act
        _service.AdoptArtefact(source, paths.PlanAbsolute);

        // Assert
        Assert.False(File.Exists(source));
        Assert.Equal("Plan", File.ReadAllText(paths.PlanAbsolute));
    }

    [Fact]
    public void AdoptArtefact_BlankArgument_Throws()
    {
        // Arrange
        var paths = NewTask("alpha");
        var source = Path.Combine(paths.TaskDirectory, "entwurf.md");
        File.WriteAllText(source, "Plan");

        // Act
        var blankSource = Record.Exception(() => _service.AdoptArtefact(" ", paths.PlanAbsolute));
        var blankTarget = Record.Exception(() => _service.AdoptArtefact(source, string.Empty));

        // Assert
        Assert.IsType<ArgumentException>(blankSource);
        Assert.IsType<ArgumentException>(blankTarget);
        Assert.Equal("Plan", File.ReadAllText(source));
        Assert.False(File.Exists(paths.PlanAbsolute));
    }
}
