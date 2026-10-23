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
/// The subtask configuration as a <em>deferred, once-only</em> capture, forced when Implementation
/// is entered and never before (requirement 1.6). Null - the default - means subtask mode is disabled,
/// so existing construction sites keep describing a normal implementation run without being edited.
/// <para>
/// Deferred rather than already taken, because requirement 1.6 lets the user change the
/// configuration while the phases before Implementation run: a snapshot taken when the request was
/// built would freeze a choice the user was still entitled to revise. <see cref="Lazy{T}"/> rather
/// than a delegate, because the orchestrator reads this member twice - in the Implementation guard
/// and again to compose the tracking paths - and a re-readable member would let the enabled flag
/// and the directory be observed from two different moments. Forcing it yields the same immutable
/// <see cref="SubtaskConfiguration"/> record forever, so nothing downstream of Implementation entry
/// can ever see a second pair, and the request still never carries
/// <see cref="ISubtaskConfiguration"/> itself.
/// </para>
/// </param>
/// <param name="SubtaskProgress">
/// Receives every subtask progress report. Null - the default - when nothing is listening, which is
/// the case for a normal implementation run.
/// </param>
/// <param name="LauncherStatus">
/// Receives a human-readable reason whenever a session refuses to paste its prompt because the
/// launcher is not running, and null again once the wait ends. Null - the default - when nothing is
/// listening, so existing construction sites keep compiling.
/// <para>
/// Deliberately its own channel rather than a reuse of the tab's general info line: that line
/// already carries independent facts (an existing task folder, a recovered run) which a launcher
/// stall must not overwrite, and clearing this status must not clear those.
/// </para>
/// </param>
/// <param name="Pause">
/// The tab's pause switch (Workflow_LOAD_AND_PAUSE spec section 6.3). While it is paused the run
/// waits at every boundary between two phases, and in an Implementation subtask run between two sessions -
/// never before the first session of the run, which the user has just asked for, never after the
/// last subtask, and never inside a session, so the running session finishes and stays live.
/// Null - the default - never holds a run, so existing construction sites are unaffected.
/// </param>
/// <param name="DesktopJobStart">
/// The tab's 'Desktop-Job jetzt starten' button (WF_DESKTOPJOBBUTTON spec section 4.3). While a run
/// waits for a desktop job's idle window, one press skips that wait for the one step that starts next;
/// the lock and screen-saver checks, the CPU check and the input watch still apply. Null - the
/// default - never skips an idle wait, so existing construction sites are unaffected.
/// </param>
public sealed record WorkflowRunRequest(
    TaskPaths Paths,
    string TaskDescription,
    ITerminalController Terminal,
    ManualPhaseSignal ManualSignal,
    IProgress<PhaseProgress> Progress,
    WorkflowPhase StartPhase = WorkflowPhase.Specification,
    Lazy<SubtaskConfiguration>? Subtasks = null,
    IProgress<SubtaskProgress>? SubtaskProgress = null,
    IProgress<string?>? LauncherStatus = null,
    PhasePauseGate? Pause = null,
    DesktopJobStartSignal? DesktopJobStart = null);

/// <summary>Drives a task through the phases of the catalogue.</summary>
public interface IWorkflowOrchestrator
{
    /// <summary>Runs the phases in catalogue order.</summary>
    /// <param name="request">Run parameters.</param>
    /// <param name="cancellationToken">Cancels the run and disposes the session.</param>
    /// <returns>A task that completes after the implementation phase is signalled.</returns>
    public Task RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken);
}
