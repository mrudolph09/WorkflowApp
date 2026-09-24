using System.IO;

namespace Workflow.Models;

/// <summary>
/// What the subtask evidence establishes about a subtask-mode task's implementation phase.
/// </summary>
/// <remarks>
/// The classification is the carrier requirement 4.6 asks for when it says the condition must be
/// <em>surfaced</em> rather than silently reinterpreted: reconciliation decides demotion from it,
/// and presentation can name the same condition without re-deriving the ledger's vocabulary - which
/// is exactly the duplication <see cref="SubtaskLedger"/> exists to prevent. Rendering it, in
/// German, belongs to the indicator and not to this type.
/// </remarks>
public enum SubtaskEvidence
{
    /// <summary>The journal does not record subtask mode, so the normal done-marker decides.</summary>
    NotApplicable,

    /// <summary>
    /// The user ended implementation through <c>Task abschliessen</c>. The ledger is not consulted
    /// at all (requirement 5.4).
    /// </summary>
    CompletedManually,

    /// <summary>A freshly read ledger reports every entry complete (requirement 4.3).</summary>
    AllComplete,

    /// <summary>A freshly read ledger reports work that is not finished. The only demoting case.</summary>
    Incomplete,

    /// <summary>
    /// The stored tracking path is blank or malformed, or the ordered index cannot be read. The
    /// recorded phase state is retained (requirement 4.6).
    /// </summary>
    Unavailable,
}

/// <summary>
/// The demote-never-promote rule that reconciles a workflow journal against the artefacts actually
/// on disk, plus the resume-phase lookup that depends on it.
/// </summary>
/// <remarks>
/// One shared helper rather than a private method on the scanner: the rule is needed by the
/// startup scan AND by the re-arm path in <c>TaskTabViewModel.SyncFolder</c>, and two copies would
/// be free to disagree. An unreconciled re-arm would resume straight into a phase whose inputs
/// have been deleted, which is precisely what this rule exists to prevent (SPEC sections 6.3
/// and 6.3.2).
/// </remarks>
public static class PhaseReconciliation
{
    /// <summary>
    /// Demotes every phase whose recorded completion is no longer backed by its artefacts - and
    /// every phase after it - to <see cref="PhaseStatus.Pending"/>.
    /// </summary>
    /// <param name="paths">The task's path set, used to locate the artefacts.</param>
    /// <param name="state">The journal, reconciled in place.</param>
    /// <returns>
    /// True when at least one phase was demoted, so the caller can persist the corrected array
    /// through <c>ITaskStateStore.ReplacePhases</c>. False means the journal already agreed with
    /// the disk and nothing needs writing.
    /// </returns>
    /// <remarks>
    /// Disk evidence may DEMOTE a phase; it may never promote one. Promotion would resurrect the
    /// hazard where a folder that merely happens to hold a spec and a plan is reported as
    /// "phase 1 done" for a task that never ran. Demotion is safe: it can only ever cause more
    /// work to be re-run, never less.
    /// </remarks>
    public static bool Reconcile(TaskPaths paths, TaskState state)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(state);

        var demoteFromHere = false;

        for (var i = 0; i < state.Phases.Count; i++)
        {
            var entry = state.Phases[i];

            if (!demoteFromHere
                && entry.Status == PhaseStatus.Completed
                && !ArtefactsPresent(paths, state, entry.Phase))
            {
                demoteFromHere = true;
            }

            if (demoteFromHere)
            {
                state.Phases[i] = new TaskPhaseState(entry.Phase, PhaseStatus.Pending, null);
            }
        }

        // The flag can only have been set by a phase that WAS Completed, so it is exactly
        // "something was demoted".
        return demoteFromHere;
    }

    /// <summary>The first phase in catalogue order that is not completed.</summary>
    /// <param name="state">The journal, already reconciled.</param>
    /// <returns>That phase, or null when every phase is completed.</returns>
    public static WorkflowPhase? FirstIncomplete(TaskState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        foreach (var entry in state.Phases)
        {
            if (entry.Status != PhaseStatus.Completed)
            {
                return entry.Phase;
            }
        }

        return null;
    }

    /// <summary>
    /// Classifies what the subtask evidence says about one task's implementation phase. This is a
    /// <em>deliberate public member beyond the one the design names</em> - <em>Presentation and
    /// Recovery</em> names only "<c>ArtefactsPresent</c> receives task state" - and it is retained
    /// rather than folded private because requirement 4.6 asks for the condition to be
    /// <em>surfaced</em>, not merely acted on. Demotion needs one bit; the user-facing layer needs
    /// to tell "unreadable" from "unfinished", and deriving that a second time outside this class
    /// is exactly the duplicated ledger vocabulary <see cref="SubtaskLedger"/> exists to prevent.
    /// </summary>
    /// <param name="paths">The task's path set. Supplies the task name the tracking folder is named after.</param>
    /// <param name="state">The journal, read for its subtask mode, tracking path and manual override.</param>
    /// <returns>The condition the evidence establishes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> or <paramref name="state"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// The rule order is fixed by the design's resolved decision for issue 9 and is not an
    /// optimisation: <c>ImplementationCompletedManually</c> is an explicit user override and is
    /// honoured <em>without consulting the ledger</em> (requirement 5.4), so a task the user stopped
    /// half-way through stays completed even though most of its subtasks never ran.
    /// </para>
    /// <para>
    /// Every remaining answer comes from <em>one</em> fresh read. <see cref="SubtaskLedger.TryRead"/>
    /// answers both questions at once - null is "no order could be established", and the counts
    /// decide completion - so there is no window in which the index can vanish between a completion
    /// probe and a readability probe and turn a retain into a demotion. Two reads were the earlier
    /// shape and their ordering was load-bearing but unpinnable; one read removes the case instead
    /// of documenting it, and halves the index I/O on the debounced re-arm path (requirements 4.5
    /// and 4.6).
    /// </para>
    /// </remarks>
    public static SubtaskEvidence Evaluate(TaskPaths paths, TaskState state)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(state);

        if (!state.SubtasksEnabled)
        {
            return SubtaskEvidence.NotApplicable;
        }

        if (state.ImplementationCompletedManually)
        {
            return SubtaskEvidence.CompletedManually;
        }

        var tracking = TryTrackingPaths(paths, state);
        if (tracking is null)
        {
            return SubtaskEvidence.Unavailable;
        }

        // A null snapshot is "the evidence could not be read", never "nothing is done": only
        // positively unfinished work may demote (requirement 4.6). Both answers come from this one
        // read, so the two can never describe different contents of the tracking repository.
        var snapshot = SubtaskLedger.TryRead(tracking);
        if (snapshot is null)
        {
            return SubtaskEvidence.Unavailable;
        }

        // Design issue 6a, the same condition the indicator's green uses: Total > 0 and every entry
        // complete. The absence of failures is not completion - an all-pending task has none.
        return snapshot.Total > 0 && snapshot.Completed == snapshot.Total
            ? SubtaskEvidence.AllComplete
            : SubtaskEvidence.Incomplete;
    }

    /// <summary>Composes the tracking path set from the stored directory, or reports it unusable.</summary>
    /// <param name="paths">The task's path set, for the task name.</param>
    /// <param name="state">The journal carrying the stored tracking directory.</param>
    /// <returns>The tracking paths, or null when the stored directory is blank or malformed.</returns>
    /// <remarks>
    /// The store writes the chosen directory verbatim - no trim, no blank-nulling - precisely so this
    /// distinction survives, and a hand-edited or older journal can carry anything. The null check is
    /// compiler-mandated rather than defensive: <c>WorkflowDirectory</c> is <c>string?</c> and the
    /// constructor's parameter is not. The constructor itself rejects empty and whitespace.
    /// </remarks>
    private static SubtaskPaths? TryTrackingPaths(TaskPaths paths, TaskState state)
    {
        var directory = state.WorkflowDirectory;

        if (directory is null)
        {
            return null;
        }

        try
        {
            return new SubtaskPaths(directory, paths.TaskName);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // ResolveReview is never demoted: its completion is baseline-relative and leaves no trace on
    // disk, so the journal is the only evidence there is.
    private static bool ArtefactsPresent(TaskPaths paths, TaskState state, WorkflowPhase phase) => phase switch
    {
        WorkflowPhase.Specification => NonEmpty(paths.SpecAbsolute) && NonEmpty(paths.PlanAbsolute),
        WorkflowPhase.Review => NonEmpty(paths.ReviewAbsolute),
        WorkflowPhase.Implementation => ImplementationPresent(paths, state),
        _ => true,
    };

    /// <summary>Applies the resolved rule table of design issue 9 to the implementation phase.</summary>
    /// <param name="paths">The task's path set.</param>
    /// <param name="state">The journal.</param>
    /// <returns>False only when the evidence positively shows unfinished work.</returns>
    /// <remarks>
    /// A subtask task never falls back to the normal done-marker: that marker is absent by design
    /// and consulting it would demote a completed task on every restart, contradicting requirement
    /// 4.3. Unavailable evidence keeps the recorded state for the same reason - unreadable is not
    /// the same as unfinished (requirement 4.6).
    /// </remarks>
    private static bool ImplementationPresent(TaskPaths paths, TaskState state) =>
        Evaluate(paths, state) switch
        {
            SubtaskEvidence.CompletedManually => true,
            SubtaskEvidence.AllComplete => true,
            SubtaskEvidence.Unavailable => true,
            SubtaskEvidence.Incomplete => false,
            _ => NonEmpty(paths.DoneAbsolute),
        };

    private static bool NonEmpty(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0;
        }
        catch (IOException)
        {
            // Unreadable is not the same as absent; do not demote on a transient lock.
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
