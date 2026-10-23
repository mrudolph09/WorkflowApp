using System.IO;
using Workflow.Services;

namespace Workflow.Tests;

/// <remarks>
/// Process-backed: runs a real pwsh. Each test returns early - and so passes without asserting -
/// on a machine without pwsh on the PATH, because xUnit 2 has no runtime skip.
/// </remarks>
public sealed class PwshDesktopJobProcessStarterTests : IDisposable
{
    private static readonly TimeSpan Exit = TimeSpan.FromSeconds(60);

    private readonly string _root;

    public PwshDesktopJobProcessStarterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wf-pwsh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static bool PwshIsAvailable() =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => File.Exists(Path.Combine(directory.Trim('"'), "pwsh.exe")));

    [Fact]
    public async Task Start_RunsTheScriptInTheWorkingDirectoryAndAppendsItsOutputToTheLog()
    {
        if (!PwshIsAvailable())
        {
            return;
        }

        var script = Path.Combine(_root, "desktopjob.probe.ps1");
        var log = Path.Combine(_root, "desktopjob.probe.log");
        await File.WriteAllTextAsync(script, "Write-Output \"cwd=$((Get-Location).Path)\"; [Console]::Error.WriteLine('fehlerzeile'); exit 3");
        await File.WriteAllTextAsync(log, "=== header ===" + Environment.NewLine);

        int exitCode;
        using (var process = new PwshDesktopJobProcessStarter().Start(new DesktopJobLaunch(script, _root, log)))
        {
            exitCode = await process.Exited.WaitAsync(Exit);
        }

        var text = await File.ReadAllTextAsync(log);
        Assert.Equal(3, exitCode);
        Assert.StartsWith("=== header ===", text, StringComparison.Ordinal);
        Assert.Contains("cwd=" + _root, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fehlerzeile", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Kill_EndsAScriptThatWouldRunForever()
    {
        if (!PwshIsAvailable())
        {
            return;
        }

        var script = Path.Combine(_root, "desktopjob.forever.ps1");
        await File.WriteAllTextAsync(script, "while ($true) { Start-Sleep -Milliseconds 100 }");

        using var process = new PwshDesktopJobProcessStarter().Start(
            new DesktopJobLaunch(script, _root, Path.Combine(_root, "desktopjob.forever.log")));

        await Task.Delay(500);
        Assert.False(process.Exited.IsCompleted);

        process.Kill();

        await process.Exited.WaitAsync(Exit);
    }
}
