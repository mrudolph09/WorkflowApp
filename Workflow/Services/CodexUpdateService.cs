using System.Diagnostics;
using Workflow.Terminal;

namespace Workflow.Services;

/// <summary>
/// Brings the Codex CLI up to date by running the vendor's install one-liner. The review phase
/// starts the <c>codex.exe</c> this installer maintains by its full path (see
/// <see cref="Models.PhaseCatalog" />), so the app is only ever as current as that binary;
/// refreshing it once per start keeps that dependency from silently ageing.
/// </summary>
public sealed class CodexUpdateService
{
    /// <summary>The installer one-liner published by OpenAI for the Codex CLI.</summary>
    public const string InstallCommand = "irm https://chatgpt.com/codex/install.ps1 | iex";

    /// <summary>How this service finds the shell to run the installer in.</summary>
    private readonly Func<string> _findShellExecutable;

    /// <summary>Creates a service that installs through the same shell the terminal tabs use.</summary>
    public CodexUpdateService()
        : this(ShellLocator.FindShellExecutable)
    {
    }

    /// <summary>
    /// Creates a service with an explicit shell lookup. Production always uses the parameterless
    /// constructor; the seam exists so a test can observe what <see cref="UpdateAsync" /> does when
    /// no shell is present, which is otherwise unreachable on a machine that has PowerShell.
    /// </summary>
    /// <param name="findShellExecutable">Returns the full path of the shell to run.</param>
    public CodexUpdateService(Func<string> findShellExecutable)
    {
        _findShellExecutable = findShellExecutable;
    }

    /// <summary>
    /// Runs the installer once and waits for it to finish. Never throws: a failed update is not a
    /// reason to disturb the user, the app simply keeps working with the CLI already installed.
    /// </summary>
    /// <param name="cancellationToken">Abandons the wait for the installer.</param>
    /// <returns>True when the installer exited with code 0, false on any failure.</returns>
    public async Task<bool> UpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var process = Process.Start(CreateStartInfo(_findShellExecutable()));

            if (process is null)
            {
                return false;
            }

            // Both pipes are redirected to keep the installer silent, which means both must be
            // drained: a full pipe buffer would block the installer before it ever exits.
            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);

            await Task.WhenAll(standardOutput, standardError);
            await process.WaitForExitAsync(cancellationToken);

            return process.ExitCode == 0;
        }
#pragma warning disable CA1031 // Startup resilience boundary: offline, a blocked proxy, a missing
        catch (Exception)      // shell and a broken installer must all degrade to "not updated".
#pragma warning restore CA1031
        {
            return false;
        }
    }

    /// <summary>
    /// Builds the installer invocation without running it. Split out of <see cref="UpdateAsync" />
    /// only so the command and its silencing flags are observable in a test - starting the real
    /// process would download from the network.
    /// </summary>
    /// <param name="shellExecutable">Full path of the shell that runs the installer.</param>
    /// <returns>The configured start information.</returns>
    public static ProcessStartInfo CreateStartInfo(string shellExecutable)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = shellExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // ArgumentList quotes each entry itself, which matters for the install command: it holds
        // spaces and a pipe that a hand-built argument string would have to escape.
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("ByPass");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(InstallCommand);

        return startInfo;
    }
}
