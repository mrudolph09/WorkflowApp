using Workflow.Models;

namespace Workflow.Tests;

public class WorkingDirectoryPathTests
{
    [Fact]
    public void Normalise_PreservesADriveRoot()
    {
        var result = WorkingDirectoryPath.Normalise(@"C:\");

        Assert.Equal(@"C:\", result);
    }

    [Fact]
    public void Normalise_StripsATrailingSeparatorFromANonRootDirectory()
    {
        var result = WorkingDirectoryPath.Normalise(@"C:\src\demo\");

        Assert.Equal(@"C:\src\demo", result);
    }

    [Fact]
    public void Normalise_LeavesAPathWithNoTrailingSeparatorUnchanged()
    {
        var result = WorkingDirectoryPath.Normalise(@"C:\src\demo");

        Assert.Equal(@"C:\src\demo", result);
    }

    [Fact]
    public void Normalise_TrimsSurroundingWhitespace()
    {
        var result = WorkingDirectoryPath.Normalise("  C:\\src\\demo  ");

        Assert.Equal(@"C:\src\demo", result);
    }

    [Fact]
    public void Normalise_ThrowsOnNullOrWhitespace()
    {
        Assert.Throws<ArgumentException>(() => WorkingDirectoryPath.Normalise("   "));
    }
}
