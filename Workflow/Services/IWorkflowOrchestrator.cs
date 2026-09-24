using Workflow.Models;

namespace Workflow.Services;

/// <summary>One workflow run: the parameters that do not change between phases.</summary>
/// <param name="Paths">The task's path set.</param>
/// <param name="TaskDescription">Plain-text task description fed into prompt 1.</param>
/// <param name="Terminal">The terminal this run drives.</param>
/// <param name="ManualSignal">Signal raised by the 'Task abschliessen' button.</param>
/// <param name="Progress">Receives every phase status change.</param>
/// <param name="StartPhase">
/// The phase the run begins at. Everything before it is skipped and never reported - a resumed
/// run's earlier indicators are painted by the tab from the journal, not by the orchestrator.
/// A defaulted positional parameter, so existing construction sites are unaffected.
/// </param>
/// <param name="Subtasks">
/// The subtask configuration as a <em>deferred, once-only</em> capture, forced when phase 4 is
/// entered and never before (requirement 1.6). Null - the default - means subtask mode is disabled,
/// so existing construction sites keep describing a normal implementation run without being edited.
/// <para>
/// Deferred rather than already taken, because requirement 1.6 lets the user change the
/// configuration while phases 1-3 run: a snapshot taken when the request was built would freeze a
/// choice the user was still entitled to revise. <see cref="Lazy{T}"/> rather than a delegate,
/// because the orchestrator reads this member twice - in the phase-4 guard and again to compose the
/// tracking paths - and a re-readable member would let the enabled flag and the directory be
/// observed from two different moments. Forcing it yields the same immutable
/// <see cref="SubtaskConfiguration"/> record forever, so nothing downstream of phase-4 entry can
/// ever see a second pair, and the request still never carries
/// <see cref="ISubtaskConfiguration"/> itself.
/// </para>
/// </param>
/// <param name="SubtaskProgress">
/// Receives every subtask progress report. Null - the default - when nothing is listening, which is
/// the case for a normal implementation run.
/// </param>
public sealed record WorkflowRunRequest(
    TaskPaths Paths,
    string TaskDescription,
    ITerminalController Terminal,
    ManualPhaseSignal ManualSignal,
    IProgress<PhaseProgress> Progress,
    WorkflowPhase StartPhase = WorkflowPhase.Specification,
    Lazy<SubtaskConfiguration>? Subtasks = null,
    IProgress<SubtaskProgress>? SubtaskProgress = null);

/// <summary>Drives a task through the four phases.</summary>
public interface IWorkflowOrchestrator
{
    /// <summary>Runs all four phases in order.</summary>
    /// <param name="request">Run parameters.</param>
    /// <param name="cancellationToken">Cancels the run and disposes the session.</param>
    /// <returns>A task that completes after the implementation phase is signalled.</returns>
    public Task RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken);
}
