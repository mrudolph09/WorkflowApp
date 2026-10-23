using System.Collections.ObjectModel;
using Workflow.Services;

namespace Workflow.Tests.Fakes;

/// <summary>Records everything the orchestrator does to a terminal.</summary>
public sealed class FakeTerminalController : ITerminalController
{
    private readonly Queue<string> _snapshots = new();

    // CA1002: a public member may not return List<T>.
    public Collection<string> StartedSessions { get; } = [];

    public Collection<string> Sent { get; } = [];

    public Collection<string> Pasted { get; } = [];

    public int ClearCount { get; private set; }

    public int DisposeCount { get; private set; }

    public DateTimeOffset LastOutputUtc { get; private set; } = DateTimeOffset.UtcNow;

    public long OutputCount { get; private set; }

    /// <summary>Set false to simulate a WebView2 page that is slow to report `ready`.</summary>
    public TaskCompletionSource ReadyGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitUntilReadyAsync(CancellationToken cancellationToken) =>
        ReadyGate.Task.WaitAsync(cancellationToken);

    /// <summary>Queues the text that the next SnapshotAsync call returns.</summary>
    public void QueueSnapshot(string text) => _snapshots.Enqueue(text);

    /// <summary>
    /// Sticky screen returned by SnapshotAsync whenever the queue is empty. Lets a test hold the
    /// terminal on one screen (e.g. "not ready", or a pasted-but-unsubmitted prompt) and then flip
    /// it, without counting individual snapshot calls.
    /// </summary>
    public string? CurrentSnapshot { get; set; }

    /// <summary>Simulates a chunk of launcher output arriving on the live session.</summary>
    public void EmitOutput()
    {
        OutputCount++;
        LastOutputUtc = DateTimeOffset.UtcNow;
    }

    public void StartSession(string executable, string arguments, string workingDirectory)
    {
        StartedSessions.Add(workingDirectory);

        // Mirrors the production contract: a fresh session has produced nothing yet, and its
        // clock starts now - never at DateTimeOffset.MinValue.
        OutputCount = 0;
        LastOutputUtc = DateTimeOffset.UtcNow;
    }

    public void ClearScreen() => ClearCount++;

    /// <summary>
    /// The nonce the orchestrator wrapped its launcher command with, captured from what was Sent.
    /// Null until that command has been written.
    /// </summary>
    public string? LauncherNonce { get; private set; }

    /// <summary>
    /// Simulates the launcher exiting back to the shell, which then prints its return marker.
    /// Every later snapshot carries that marker, as a real pseudo-console would once the primary
    /// screen buffer is restored.
    /// </summary>
    public void SimulateLauncherExit()
    {
        if (LauncherNonce is null)
        {
            throw new InvalidOperationException("The launcher command has not been sent yet.");
        }

        _launcherExited = true;
        EmitOutput();
    }

    /// <summary>
    /// Simulates the user starting a launcher by hand after the session stalled: the new TUI takes
    /// the alternate screen buffer, so the shell's return marker is no longer rendered.
    /// </summary>
    public void SimulateLauncherRelaunch() => _launcherExited = false;

    /// <summary>
    /// Suppresses both markers, modelling a shell that never executed the line that was typed into
    /// it. Production always prints the run marker, so this is the one state a test has to ask for.
    /// </summary>
    public bool SuppressMarkers { get; set; }

    private bool _launcherExited;

    /// <summary>
    /// Mirrors the shell, not just the test's script: a real session prints the run marker the
    /// instant it executes the line we typed, and the return marker when the launcher gives the
    /// terminal back. Prefixing them here is what makes an ordinary test represent "a launcher is
    /// running" rather than "the shell never ran anything", which is the state the orchestrator now
    /// refuses to paste into.
    /// </summary>
    private string WithMarkers(string screen)
    {
        if (LauncherNonce is null || SuppressMarkers)
        {
            return screen;
        }

        var marker = (_launcherExited ? "WF-RET-" : "WF-RUN-") + LauncherNonce;
        return marker + "\n" + screen;
    }

    public Task<string> SnapshotAsync(int lines, CancellationToken cancellationToken) =>
        Task.FromResult(WithMarkers(_snapshots.Count > 0 ? _snapshots.Dequeue() : CurrentSnapshot ?? string.Empty));

    public void Send(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Captured BEFORE any emission, so a test can ask what the terminal had produced at the
        // moment this write happened.
        OutputCountAtSend.Add(OutputCount);
        Sent.Add(text);

        var runMarker = text.IndexOf("('WF-RUN-' + '", StringComparison.Ordinal);
        if (runMarker >= 0)
        {
            var start = runMarker + "('WF-RUN-' + '".Length;
            var end = text.IndexOf('\'', start);
            if (end > start)
            {
                LauncherNonce = text[start..end];
                _launcherExited = false;
            }
        }

        if (EmitOutputOnNextCarriageReturn && text == "\r")
        {
            EmitOutput();
        }

        if (text == "\r")
        {
            OnCarriageReturn?.Invoke();
        }
    }

    /// <summary>
    /// Runs synchronously inside every bare carriage-return write, after it has been recorded. Lets a
    /// test script exactly what the launcher does with that one key press - start working, absorb it,
    /// or emit unrelated output later - instead of an always-on reaction.
    /// </summary>
    public Action? OnCarriageReturn { get; set; }

    /// <summary>Runs synchronously inside every SendPaste call, after the body has been recorded.</summary>
    public Action? OnPaste { get; set; }

    /// <summary>Number of bare carriage returns written so far, i.e. submit attempts.</summary>
    public int CarriageReturnCount => Sent.Count(text => text == "\r");

    /// <summary>When true, a bare carriage return produces output, as a live launcher would.</summary>
    public bool EmitOutputOnNextCarriageReturn { get; set; }

    /// <summary>
    /// When set, SendPaste schedules one chunk of output after this delay instead of producing
    /// none, mimicking the PTY echoing the bracketed-paste block back asynchronously.
    /// </summary>
    public TimeSpan PasteEchoDelay { get; set; }

    /// <summary>OutputCount as it stood when each entry of <see cref="Sent"/> was written.</summary>
    public Collection<long> OutputCountAtSend { get; } = [];

    public void SendPaste(string body)
    {
        Pasted.Add(body);
        OnPaste?.Invoke();

        if (PasteEchoDelay <= TimeSpan.Zero)
        {
            return;
        }

        // Deliberately fire-and-forget. The production PTY echo arrives well after SendPaste has
        // returned, and that asynchrony is exactly what the paste-gate test needs to reproduce.
        _ = Task.Run(async () =>
        {
            await Task.Delay(PasteEchoDelay);
            EmitOutput();
        });
    }

    /// <summary>
    /// Moves the last output back by <paramref name="by"/>, as if the session had been silent that
    /// long. Lets a stall test reach the watchdog's threshold without waiting for it.
    /// </summary>
    public void BackdateOutput(TimeSpan by) => LastOutputUtc -= by;

    /// <summary>
    /// Runs synchronously inside every DisposeSession call, before it is counted. Lets a test record
    /// what was on disk at the moment the session was killed - the order of the kill relative to the
    /// application's own file writes.
    /// </summary>
    public Action? OnDisposeSession { get; set; }

    public void DisposeSession()
    {
        OnDisposeSession?.Invoke();
        DisposeCount++;
    }

    /// <summary>What StopSessionAsync reports: true for a process tree confirmed gone.</summary>
    public bool StopConfirms { get; set; } = true;

    /// <summary>Number of StopSessionAsync calls so far.</summary>
    public int StopCount { get; private set; }

    /// <summary>Runs synchronously inside every StopSessionAsync call, before it is counted.</summary>
    public Action? OnStopSession { get; set; }

    public Task<bool> StopSessionAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OnStopSession?.Invoke();
        StopCount++;

        return Task.FromResult(StopConfirms);
    }
}
