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

    public Task<string> SnapshotAsync(int lines, CancellationToken cancellationToken) =>
        Task.FromResult(_snapshots.Count > 0 ? _snapshots.Dequeue() : string.Empty);

    public void Send(string text) => Sent.Add(text);

    /// <summary>How many pastes had their submitting CR written before a new session started.</summary>
    public int SubmitsCompletedBeforeNextSession { get; private set; }

    public Task SendPasteAsync(string body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Pasted.Add(body);
        Sent.Add("\r");
        SubmitsCompletedBeforeNextSession++;
        return Task.CompletedTask;
    }

    public void DisposeSession() => DisposeCount++;
}
