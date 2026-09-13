namespace Workflow.Services;

/// <summary>
/// Everything the orchestrator is allowed to do to a terminal. Implemented by TerminalViewModel
/// over WebView2 plus ConPtySession, and by a fake in the tests.
/// </summary>
public interface ITerminalController
{
    /// <summary>UTC timestamp of the most recent chunk of PTY output.</summary>
    /// <remarks>
    /// Reset to "now" by <see cref="StartSession"/>. It must never be left at
    /// <see cref="DateTimeOffset.MinValue"/> on a live session: the settle loop would then read
    /// an enormous idle time on its very first iteration, snapshot an empty screen and send the
    /// prompt before the launcher had drawn anything (spec section 7.3, Gate A).
    /// </remarks>
    public DateTimeOffset LastOutputUtc { get; }

    /// <summary>Number of output chunks received on the current session; 0 until the first one.</summary>
    /// <remarks>Gate A uses this to tell "nothing has happened yet" from "the screen is quiet".</remarks>
    public long OutputCount { get; }

    /// <summary>Completes once the terminal page is constructed and listening (spec section 6.4).</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when the terminal may be driven.</returns>
    public Task WaitUntilReadyAsync(CancellationToken cancellationToken);

    /// <summary>Disposes any previous session and starts a fresh one.</summary>
    /// <param name="executable">Shell executable.</param>
    /// <param name="arguments">Shell arguments.</param>
    /// <param name="workingDirectory">Initial current directory.</param>
    public void StartSession(string executable, string arguments, string workingDirectory);

    /// <summary>Clears the rendered terminal, used between phases.</summary>
    public void ClearScreen();

    /// <summary>Reads the last rendered rows of the terminal.</summary>
    /// <param name="lines">How many rows to read from the end of the buffer.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The rendered text, rows joined by newline.</returns>
    public Task<string> SnapshotAsync(int lines, CancellationToken cancellationToken);

    /// <summary>Writes UTF-8 encoded text to the pseudo-console.</summary>
    /// <param name="text">Text to send verbatim, including any control characters.</param>
    public void Send(string text);

    /// <summary>
    /// Writes a bracketed-paste block and then, after a short delay, the submitting CR.
    /// </summary>
    /// <param name="body">The prompt text.</param>
    /// <param name="cancellationToken">Cancels the pending submit.</param>
    /// <returns>A task that completes once the submitting CR has been written.</returns>
    /// <remarks>
    /// Awaitable and session-scoped on purpose. A fire-and-forget delay that later calls
    /// <c>Send(CR)</c> on whatever session is current can deliver the carriage return into the
    /// *next* phase's launcher - reachable whenever a reused task folder already satisfies a watcher
    /// (spec section 8.3) - where it would accept `yo`'s preselected "No, exit".
    /// </remarks>
    public Task SendPasteAsync(string body, CancellationToken cancellationToken);

    /// <summary>Disposes the current session, killing its process tree.</summary>
    public void DisposeSession();
}
