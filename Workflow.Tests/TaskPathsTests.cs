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
}
