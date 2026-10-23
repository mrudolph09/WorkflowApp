using Workflow.Services;

namespace Workflow.Tests.Fakes;

/// <summary>
/// A run that is held at a phase boundary: it awaits the pause gate the tab handed over, exactly as
/// the real orchestrator does between phases, and exposes the run task so a test can observe how
/// the run ended. It never creates a gate of its own.
/// </summary>
public sealed class HeldRunOrchestrator : IWorkflowOrchestrator
{
    public WorkflowRunRequest? Request { get; private set; }

    /// <summary>The task of the held run; null until the tab has started one.</summary>
    public Task? RunTask { get; private set; }

    public Task RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Request = request;
        var gate = request.Pause ?? throw new InvalidOperationException("The tab handed the run no pause gate.");
        RunTask = HoldAsync(gate, cancellationToken);
        return RunTask;
    }

    private static async Task HoldAsync(PhasePauseGate gate, CancellationToken cancellationToken)
    {
        await gate.WaitWhilePausedAsync(cancellationToken);

        // Released by Unpause: the run simply ends, as one whose next phase has nothing left to do.
    }
}
