namespace Workflow.Services;

/// <summary>What one desktop job step launches.</summary>
/// <param name="ScriptPath">Absolute path of the PowerShell script.</param>
/// <param name="WorkingDirectory">The product directory the agent sessions run in.</param>
/// <param name="LogPath">The step's log; stdout and stderr are appended to it.</param>
public sealed record DesktopJobLaunch(string ScriptPath, string WorkingDirectory, string LogPath);

/// <summary>Starts the process of one desktop job step.</summary>
public interface IDesktopJobProcessStarter
{
    /// <summary>Starts the step's script.</summary>
    /// <param name="launch">What to start.</param>
    /// <returns>The running process.</returns>
    /// <exception cref="System.ComponentModel.Win32Exception">The process could not be started.</exception>
    /// <exception cref="InvalidOperationException">The process could not be started.</exception>
    /// <exception cref="System.IO.IOException">The log could not be opened.</exception>
    /// <exception cref="UnauthorizedAccessException">The log could not be opened.</exception>
    /// <exception cref="DesktopJobStartException">
    /// The process started but could not be contained, and was killed again; the message is the reason.
    /// </exception>
    public IDesktopJobProcess Start(DesktopJobLaunch launch);
}

/// <summary>A running desktop job process. Disposing it kills whatever is still running.</summary>
public interface IDesktopJobProcess : IDisposable
{
    /// <summary>Completes with the exit code once the process has exited.</summary>
    public Task<int> Exited { get; }

    /// <summary>Kills the process and every process it started.</summary>
    public void Kill();

    /// <summary>
    /// Kills the process and everything it started - also after a normal exit, which may leave an
    /// application under test behind - and waits until none of them runs any more.
    /// </summary>
    /// <param name="timeout">How long to keep killing and waiting.</param>
    /// <returns>True when the whole tree is confirmed gone; false when the timeout passed first.</returns>
    public Task<bool> TerminateAsync(TimeSpan timeout);
}
