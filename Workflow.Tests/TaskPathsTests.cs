using Workflow.Models;

namespace Workflow.Tests;

public class TaskPathsTests
{
    // A drive root is a legal directory-picker result. Normalising it with
    // TrimEnd(Path.DirectorySeparatorChar) yields "C:", which Windows reads as the DRIVE-RELATIVE
    // current directory, so Path.Combine("C:", "my-task") is "C:my-task" - a different folder.
    [Fact]
    public void TaskDirectory_PreservesADriveRoot()
    {
        var paths = new TaskPaths(@"C:\", "my-task");

        Assert.Equal(@"C:\", paths.WorkingDirectory);
        Assert.Equal(@"C:\my-task", paths.TaskDirectory);
        Assert.Equal(@"C:\my-task\my-task_spec.md", paths.SpecAbsolute);
    }

    [Fact]
    public void TaskDirectory_StripsATrailingSeparatorFromANonRootDirectory()
    {
        var paths = new TaskPaths(@"C:\src\demo\", "my-task");

        Assert.Equal(@"C:\src\demo", paths.WorkingDirectory);
        Assert.Equal(@"C:\src\demo\my-task", paths.TaskDirectory);
    }

    [Fact]
    public void TaskDirectory_IsWorkingDirectoryPlusTaskName()
    {
        var paths = new TaskPaths(@"C:\src\demo", "my-task");

        Assert.Equal(@"C:\src\demo\my-task", paths.TaskDirectory);
    }

    [Fact]
    public void AbsolutePaths_UseTaskNameForBothFolderAndFile()
    {
        var paths = new TaskPaths(@"C:\src\demo", "my-task");

        Assert.Equal(@"C:\src\demo\my-task\my-task_spec.md", paths.SpecAbsolute);
        Assert.Equal(@"C:\src\demo\my-task\my-task_plan.md", paths.PlanAbsolute);
        Assert.Equal(@"C:\src\demo\my-task\my-task-review.md", paths.ReviewAbsolute);
    }

    [Fact]
    public void RelativePaths_UseForwardSlashesAndLeadingDot()
    {
        var paths = new TaskPaths(@"C:\src\demo", "my-task");

        Assert.Equal("./my-task/my-task_spec.md", paths.SpecRelative);
        Assert.Equal("./my-task/my-task_plan.md", paths.PlanRelative);
        Assert.Equal("./my-task/my-task-review.md", paths.ReviewRelative);
    }

    [Fact]
    public void WorkingDirectory_TrailingSeparatorIsStripped()
    {
        var paths = new TaskPaths(@"C:\src\demo\", "my-task");

        Assert.Equal(@"C:\src\demo", paths.WorkingDirectory);
        Assert.Equal(@"C:\src\demo\my-task", paths.TaskDirectory);
    }

    [Fact]
    public void Names_WithSpacesAndUmlauts_AreCarriedThroughVerbatim()
    {
        var paths = new TaskPaths(@"C:\src\demo", "Größe prüfen");

        Assert.Equal(@"C:\src\demo\Größe prüfen\Größe prüfen_spec.md", paths.SpecAbsolute);
        Assert.Equal("./Größe prüfen/Größe prüfen_spec.md", paths.SpecRelative);
    }

    [Fact]
    public void TaskName_IsTrimmed()
    {
        var paths = new TaskPaths(@"C:\src\demo", "  my-task  ");

        Assert.Equal("my-task", paths.TaskName);
    }

    [Fact]
    public void Constructor_DerivesTheJournalAndDoneMarkerPaths()
    {
        var paths = new TaskPaths(@"C:\work", "Feature X");

        Assert.Equal(@"C:\work\Feature X\.workflow-state.json", paths.StateAbsolute);
        Assert.Equal(@"C:\work\Feature X\Feature X-done.md", paths.DoneAbsolute);
        Assert.Equal("./Feature X/Feature X-done.md", paths.DoneRelative);
    }

    [Fact]
    public void SimplifiedAbsolute_IsTheHyphenatedReportInTheTaskFolder()
    {
        // Arrange
        var paths = new TaskPaths(@"C:\work", "Feature X");

        // Act
        var simplified = paths.SimplifiedAbsolute;

        // Assert
        Assert.Equal(@"C:\work\Feature X\Feature X-simplified.md", simplified);
    }

    [Fact]
    public void SimplifiedRelative_UsesForwardSlashesAndLeadingDot()
    {
        // Arrange
        var paths = new TaskPaths(@"C:\work", "Feature X");

        // Act
        var simplified = paths.SimplifiedRelative;

        // Assert
        Assert.Equal("./Feature X/Feature X-simplified.md", simplified);
    }

    // --- FromTaskDirectory: the folder 'Task laden' was given (Workflow_LOAD_AND_PAUSE spec 5.4) ---

    [Theory]
    [InlineData(@"C:\work\alpha", @"C:\work", "alpha", @"C:\work\alpha")]
    [InlineData(@"C:\work\alpha\", @"C:\work", "alpha", @"C:\work\alpha")]
    [InlineData("C:/work/alpha", @"C:\work", "alpha", @"C:\work\alpha")]
    [InlineData(@"C:\work\.\alpha", @"C:\work", "alpha", @"C:\work\alpha")]
    [InlineData(@"C:\work\sub\..\alpha", @"C:\work", "alpha", @"C:\work\alpha")]
    [InlineData(@"C:\alpha", @"C:\", "alpha", @"C:\alpha")]
    [InlineData(@"\\server\share\alpha", @"\\server\share", "alpha", @"\\server\share\alpha")]
    [InlineData(@"C:\Arbeit\Feature Ü 1", @"C:\Arbeit", "Feature Ü 1", @"C:\Arbeit\Feature Ü 1")]
    public void FromTaskDirectory_SplitsTheFolderIntoWorkingDirectoryAndTaskName(
        string taskDirectory, string workingDirectory, string taskName, string expectedTaskDirectory)
    {
        // Act
        var paths = TaskPaths.FromTaskDirectory(taskDirectory);

        // Assert
        Assert.NotNull(paths);
        Assert.Equal(workingDirectory, paths.WorkingDirectory);
        Assert.Equal(taskName, paths.TaskName);
        Assert.Equal(expectedTaskDirectory, paths.TaskDirectory);
    }

    // Not fully qualified, a device path, a drive or share root (no parent to be the working
    // directory), or a folder name with leading whitespace (the constructor would trim it into a
    // DIFFERENT folder) can never be a task folder.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"C:\")]
    [InlineData("C:")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\server\share\")]
    [InlineData("alpha")]
    [InlineData(@"work\alpha")]
    [InlineData(@".\alpha")]
    [InlineData(@"..\alpha")]
    [InlineData(@"\alpha")]
    [InlineData("C:alpha")]
    [InlineData(@"\\?\C:\work\alpha")]
    [InlineData(@"\\.\C:\work\alpha")]
    [InlineData(@"C:\work\ alpha")]
    public void FromTaskDirectory_APathThatCannotBeATaskFolder_ReturnsNull(string? taskDirectory)
    {
        // Act
        var paths = TaskPaths.FromTaskDirectory(taskDirectory);

        // Assert
        Assert.Null(paths);
    }
}
