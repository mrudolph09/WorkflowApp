using System.IO;
using Workflow.Models;

namespace Workflow.Tests;

public class SubtaskPathsTests
{
    private const string Root = @"C:\repo\Workflows";

    [Fact]
    public void Constructor_ComposesEveryLocationFromTheRootAndTheTaskName()
    {
        var paths = new SubtaskPaths(Root, "demo");

        Assert.Equal(Root, paths.WorkflowDirectory);
        Assert.Equal("demo", paths.TaskName);
        Assert.Equal(@"C:\repo\Workflows\demo", paths.TaskDirectory);
        Assert.Equal(@"C:\repo\Workflows\demo\result.json", paths.ResultAbsolute);
        Assert.Equal(@"C:\repo\Workflows\demo\subtasks", paths.SubtasksDirectory);
        Assert.Equal(@"C:\repo\Workflows\task_template", paths.TemplateDirectory);
    }

    [Fact]
    public void TemplateFolderName_IsTheTrackingRepositoryMarker()
    {
        Assert.Equal("task_template", SubtaskPaths.TemplateFolderName);
        Assert.Equal(
            Path.Combine(Root, SubtaskPaths.TemplateFolderName),
            new SubtaskPaths(Root, "demo").TemplateDirectory);
    }

    // Requirement 6.6 / design issue 11: every substituted path token is ABSOLUTE, including the
    // task folder, so no prompt and no consumer composes a root plus a relative fragment. The
    // {subtask_path} token therefore carries the absolute subtasks directory, not "demo\subtasks".
    [Fact]
    public void SubtaskPathToken_IsTheAbsoluteSubtasksDirectory()
    {
        var paths = new SubtaskPaths(Root, "demo");

        Assert.Equal(@"C:\repo\Workflows\demo\subtasks", paths.SubtaskPathToken);
        Assert.Equal(paths.SubtasksDirectory, paths.SubtaskPathToken);
        Assert.Equal(paths.SubtaskDirectory("ST-001"), Path.Combine(paths.SubtaskPathToken, "ST-001"));
    }

    [Fact]
    public void EverySubstitutedPath_IsFullyQualified()
    {
        var paths = new SubtaskPaths(Root, "demo");

        Assert.All(
            new[]
            {
                paths.WorkflowDirectory,
                paths.TaskDirectory,
                paths.ResultAbsolute,
                paths.SubtasksDirectory,
                paths.TemplateDirectory,
                paths.SubtaskPathToken,
                paths.SubtaskDirectory("ST-001"),
                paths.SubtaskMarkdown("ST-001"),
                paths.SubtaskStatusFile("ST-001"),
                paths.SubtaskResultFile("ST-001"),
            },
            path => Assert.True(Path.IsPathFullyQualified(path), $"not absolute: {path}"));
    }

    [Fact]
    public void SubtaskFileAccessors_PointIntoThatSubtasksOwnFolder()
    {
        var paths = new SubtaskPaths(Root, "demo");

        Assert.Equal(@"C:\repo\Workflows\demo\subtasks\ST-001", paths.SubtaskDirectory("ST-001"));
        Assert.Equal(@"C:\repo\Workflows\demo\subtasks\ST-001\subtask.md", paths.SubtaskMarkdown("ST-001"));
        Assert.Equal(@"C:\repo\Workflows\demo\subtasks\ST-001\status.json", paths.SubtaskStatusFile("ST-001"));
        Assert.Equal(@"C:\repo\Workflows\demo\subtasks\ST-001\result.json", paths.SubtaskResultFile("ST-001"));
    }

    // A drive root is a legal picker result: TrimEnd of the separator would turn a drive root into
    // the DRIVE-RELATIVE "C:", so Path.Combine would silently address a different folder.
    [Fact]
    public void Constructor_PreservesADriveRoot()
    {
        var paths = new SubtaskPaths(@"C:\", "demo");

        Assert.Equal(@"C:\", paths.WorkflowDirectory);
        Assert.Equal(@"C:\demo", paths.TaskDirectory);
        Assert.Equal(@"C:\task_template", paths.TemplateDirectory);
    }

    [Fact]
    public void Constructor_NormalisesTheRootAndTrimsTheTaskName()
    {
        var paths = new SubtaskPaths(@"C:\repo\Workflows\", "  demo  ");

        Assert.Equal(@"C:\repo\Workflows", paths.WorkflowDirectory);
        Assert.Equal("demo", paths.TaskName);
        Assert.Equal(@"C:\repo\Workflows\demo", paths.TaskDirectory);
    }

    // ThrowsAny: ArgumentException.ThrowIfNullOrWhiteSpace raises the derived
    // ArgumentNullException for null and ArgumentException for blank - the same contract TaskPaths
    // already has.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsABlankRoot(string? workflowDirectory) =>
        Assert.ThrowsAny<ArgumentException>(() => new SubtaskPaths(workflowDirectory!, "demo"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsABlankTaskName(string? taskName) =>
        Assert.ThrowsAny<ArgumentException>(() => new SubtaskPaths(Root, taskName!));

    [Theory]
    [InlineData("ST-001-compose-tracking-paths")]
    [InlineData("ST_002")]
    [InlineData("a")]
    [InlineData("subtask with spaces")]
    // Requirement 2.10: ONLY the exact dot-names are unsafe. A name that merely CONTAINS two dots
    // is an ordinary folder name and must stay executable.
    [InlineData("ST-003-retry..fallback")]
    [InlineData("..leading")]
    [InlineData("trailing..")]
    [InlineData("...")]
    public void IsValidTitle_AcceptsASafeSingleSegment(string title) =>
        Assert.True(SubtaskPaths.IsValidTitle(title));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("  ..  ")]
    [InlineData(@"..\..\Windows")]
    [InlineData("../../Windows")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    [InlineData("C:")]
    [InlineData("has:colon")]
    [InlineData("has|pipe")]
    [InlineData("has*star")]
    public void IsValidTitle_RejectsAnythingThatCouldEscapeTheTrackingRepository(string? title) =>
        Assert.False(SubtaskPaths.IsValidTitle(title));

    [Fact]
    public void IsValidTitle_TrimsBeforeValidating()
    {
        Assert.True(SubtaskPaths.IsValidTitle("  ST-001  "));
        Assert.Equal(
            @"C:\repo\Workflows\demo\subtasks\ST-001",
            new SubtaskPaths(Root, "demo").SubtaskDirectory("  ST-001  "));
    }

    [Theory]
    [InlineData(@"..\escape")]
    [InlineData("a/b")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("   ")]
    public void SubtaskAccessors_RejectAnUnsafeTitle(string title)
    {
        var paths = new SubtaskPaths(Root, "demo");

        Assert.Throws<ArgumentException>(() => paths.SubtaskDirectory(title));
        Assert.Throws<ArgumentException>(() => paths.SubtaskMarkdown(title));
        Assert.Throws<ArgumentException>(() => paths.SubtaskStatusFile(title));
        Assert.Throws<ArgumentException>(() => paths.SubtaskResultFile(title));
    }

    // Requirement 2.10 / task observable: the rejection happens BEFORE any filesystem access, so
    // nothing outside the tracking repository is probed, created or enumerated. The root here is a
    // real directory: if an accessor touched the disk at all, the escape target would appear.
    [Fact]
    public void UnsafeTitle_IsRejectedWithoutTouchingTheFilesystem()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "SubtaskPathsTests", Guid.NewGuid().ToString("N"));
        var root = Path.Combine(sandbox, "Workflows");
        var outside = Path.Combine(sandbox, "escape");
        Directory.CreateDirectory(root);

        try
        {
            var paths = new SubtaskPaths(root, "demo");

            Assert.Throws<ArgumentException>(() => paths.SubtaskStatusFile(@"..\..\escape"));

            Assert.False(Directory.Exists(outside));
            Assert.False(Directory.Exists(paths.TaskDirectory));
            Assert.False(Directory.Exists(paths.SubtasksDirectory));
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    // The path model never creates anything: composing paths for a root that does not exist must
    // succeed and must leave the disk untouched.
    [Fact]
    public void Composition_NeverCreatesAnything()
    {
        var root = Path.Combine(Path.GetTempPath(), "SubtaskPathsTests", Guid.NewGuid().ToString("N"));

        var paths = new SubtaskPaths(root, "demo");

        Assert.Equal(
            Path.Combine(root, "demo", "subtasks", "ST-001", "status.json"),
            paths.SubtaskStatusFile("ST-001"));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void EverySubtaskPath_StaysUnderTheSubtasksDirectory()
    {
        var paths = new SubtaskPaths(Root, "demo");

        Assert.All(
            new[]
            {
                paths.SubtaskDirectory("ST-003-retry..fallback"),
                paths.SubtaskMarkdown("ST-003-retry..fallback"),
                paths.SubtaskStatusFile("ST-003-retry..fallback"),
                paths.SubtaskResultFile("ST-003-retry..fallback"),
            },
            path =>
            {
                Assert.StartsWith(
                    paths.SubtasksDirectory + Path.DirectorySeparatorChar,
                    path,
                    StringComparison.Ordinal);
                Assert.Equal(Path.GetFullPath(path), path);
            });
    }
}
