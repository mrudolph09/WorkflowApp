using System.IO;

namespace Workflow.Models;

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
                && !ArtefactsPresent(paths, entry.Phase))
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

    // ResolveReview is never demoted: its completion is baseline-relative and leaves no trace on
    // disk, so the journal is the only evidence there is.
    private static bool ArtefactsPresent(TaskPaths paths, WorkflowPhase phase) => phase switch
    {
        WorkflowPhase.Specification => NonEmpty(paths.SpecAbsolute) && NonEmpty(paths.PlanAbsolute),
        WorkflowPhase.Review => NonEmpty(paths.ReviewAbsolute),
        WorkflowPhase.Implementation => NonEmpty(paths.DoneAbsolute),
        _ => true,
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
