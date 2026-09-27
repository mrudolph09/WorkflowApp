using System.IO;
using Workflow.Services;

namespace Workflow.Tests;

public class CodexUpdateServiceTests
{
    private const string Pwsh = @"C:\Program Files\PowerShell\7\pwsh.exe";

    [Fact]
    public void CreateStartInfo_ForAnyShell_RunsTheCodexInstallerOneLiner()
    {
        // Arrange
        var expected = new[]
        {
            "-ExecutionPolicy",
            "ByPass",
            "-c",
            "irm https://chatgpt.com/codex/install.ps1 | iex",
        };

        // Act
        var startInfo = CodexUpdateService.CreateStartInfo(Pwsh);

        // Assert
        Assert.Equal(Pwsh, startInfo.FileName);
        Assert.Equal(expected, startInfo.ArgumentList);
    }

    [Fact]
    public void CreateStartInfo_ForAnyShell_KeepsTheInstallerInvisibleAndDrainable()
    {
        // Arrange
        // (no arrangement: the start info is built from the shell path alone)

        // Act
        var startInfo = CodexUpdateService.CreateStartInfo(Pwsh);

        // Assert
        Assert.True(startInfo.CreateNoWindow, "The installer must not flash a console window at startup.");
        Assert.False(startInfo.UseShellExecute, "Redirection requires UseShellExecute to be false.");
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
    }

    [Fact]
    public async Task UpdateAsync_WhenNoShellIsFound_ReturnsFalseInsteadOfThrowing()
    {
        // Arrange
        var service = new CodexUpdateService(
            () => throw new FileNotFoundException("Weder pwsh.exe noch powershell.exe wurden gefunden."));

        // Act
        var updated = await service.UpdateAsync();

        // Assert
        Assert.False(updated);
    }

    [Fact]
    public async Task UpdateAsync_WhenTheShellPathIsInvalid_ReturnsFalseInsteadOfThrowing()
    {
        // Arrange
        var service = new CodexUpdateService(() => @"C:\does\not\exist\no-such-shell.exe");

        // Act
        var updated = await service.UpdateAsync();

        // Assert
        Assert.False(updated);
    }
}
