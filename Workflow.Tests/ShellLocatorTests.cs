using System.IO;
using Workflow.Terminal;

namespace Workflow.Tests;

public class ShellLocatorTests
{
    [Fact]
    public void FindShellExecutable_ReturnsAnExistingExecutable()
    {
        var shell = ShellLocator.FindShellExecutable();

        Assert.True(File.Exists(shell), $"Shell not found at {shell}");
        Assert.EndsWith(".exe", shell, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShellArguments_SuppressTheLogoAndKeepTheShellOpen()
    {
        Assert.Contains("-NoLogo", ShellLocator.ShellArguments, StringComparison.Ordinal);
        Assert.Contains("-NoExit", ShellLocator.ShellArguments, StringComparison.Ordinal);
    }
}
