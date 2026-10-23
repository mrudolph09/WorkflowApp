using System.Text.RegularExpressions;

namespace Workflow.Tests;

public class BuildInfoTests
{
    [Fact]
    public void Version_IsGeneratedFromTheBuildNumberFile()
    {
        // Workflow.csproj increments Workflow\build.number on every build and generates
        // BuildInfo.g.cs from it; the shell shows this value top-right.
        Assert.Matches(new Regex(@"^1\.0\.[1-9]\d*$"), BuildInfo.Version);
    }
}
