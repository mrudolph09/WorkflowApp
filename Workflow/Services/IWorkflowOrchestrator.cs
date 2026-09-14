using Workflow.Models;

namespace Workflow.Services;

/// <summary>One workflow run: the parameters that do not change between phases.</summary>
/// <param name="Paths">The task's path set.</param>
/// <param name="TaskDescription">Plain-text task description fed into prompt 1.</param>
/// <param name="Terminal">The terminal this run drives.</param>
/// <param name="ManualSignal">Signal raised by the 'Phase abschliessen' / 'Task abschliessen' buttons.</param>
/// <param name="Progress">Receives every phase status change.</param>
/// <param name="StartPhase">
/// The phase the run begins at. Everything before it is skipped and never reported - a resumed
/// run's earlier indicators are painted by the tab from the journal, not by the orchestrator.
/// A defaulted positional parameter, so existing construction sites are unaffected.
/// </param>
public sealed record WorkflowRunRequest(
    TaskPaths Paths,
    string TaskDescription,
    ITerminalController Terminal,
    ManualPhaseSignal ManualSignal,
    IProgress<PhaseProgress> Progress,
    WorkflowPhase StartPhase = WorkflowPhase.Specification);

/// <summary>Drives a task through the four phases.</summary>
public interface IWorkflowOrchestrator
{
    /// <summary>Runs all four phases in order.</summary>
    /// <param name="request">Run parameters.</param>
    /// <param name="cancellationToken">Cancels the run and disposes the session.</param>
    /// <returns>A task that completes after the implementation phase is signalled.</returns>
    public Task RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken);
}
