namespace Workflow.Models;

/// <summary>The four things the subtask loop can be told to do next.</summary>
public enum SubtaskPlanKind
{
    /// <summary>Start a session for <see cref="SubtaskPlan.Next"/>.</summary>
    Runnable,

    /// <summary>
    /// Nothing is runnable, but deferred entries wait for their desktop job. The loop polls for the
    /// idle window and runs the job; without a desktop job runner the run ends with a distinct message.
    /// </summary>
    WaitingForDesktopWindow,

    /// <summary>Nothing is runnable or waiting for a desktop job, and not everything is complete.</summary>
    TerminallyBlocked,

    /// <summary>Every entry is complete, or there are none.</summary>
    Done,
}

/// <summary>
/// What the subtask loop should do next, as decided by <see cref="SubtaskScheduler.Plan"/>. Built
/// only through the factories below, so each kind carries exactly the data that belongs to it.
/// </summary>
public sealed record SubtaskPlan
{
    private SubtaskPlan(
        SubtaskPlanKind kind,
        SubtaskState? next = null,
        IReadOnlyList<string>? titles = null,
        IReadOnlyList<string>? reasons = null)
    {
        Kind = kind;
        Next = next;
        Titles = titles ?? [];
        Reasons = reasons ?? [];
    }

    /// <summary>The plan for a run in which every entry is complete.</summary>
    public static SubtaskPlan Done { get; } = new(SubtaskPlanKind.Done);

    /// <summary>Which of the four outcomes this is.</summary>
    public SubtaskPlanKind Kind { get; }

    /// <summary>
    /// For <see cref="SubtaskPlanKind.Runnable"/>, the first entry in index order that may be
    /// attempted now; otherwise null.
    /// </summary>
    public SubtaskState? Next { get; }

    /// <summary>
    /// For <see cref="SubtaskPlanKind.WaitingForDesktopWindow"/>, the deferred entries whose
    /// dependencies are complete, in index order; otherwise empty.
    /// </summary>
    public IReadOnlyList<string> Titles { get; }

    /// <summary>
    /// For <see cref="SubtaskPlanKind.TerminallyBlocked"/>, human-readable German lines, one per
    /// entry that cannot run, such as <c>ST-015 wartet auf ST-014 (waiting_for_human: Freigabe
    /// fehlt)</c>; otherwise empty.
    /// </summary>
    public IReadOnlyList<string> Reasons { get; }

    /// <summary>Creates the plan that starts a session for <paramref name="next"/>.</summary>
    /// <param name="next">The entry to attempt.</param>
    /// <returns>The plan.</returns>
    public static SubtaskPlan Runnable(SubtaskState next) => new(SubtaskPlanKind.Runnable, next: next);

    /// <summary>Creates the plan that waits for the desktop jobs of <paramref name="titles"/>.</summary>
    /// <param name="titles">The deferred entries, in index order.</param>
    /// <returns>The plan.</returns>
    public static SubtaskPlan WaitingForDesktopWindow(IReadOnlyList<string> titles) =>
        new(SubtaskPlanKind.WaitingForDesktopWindow, titles: titles);

    /// <summary>Creates the plan that ends the run for <paramref name="reasons"/>.</summary>
    /// <param name="reasons">Why nothing can run.</param>
    /// <returns>The plan.</returns>
    public static SubtaskPlan TerminallyBlocked(IReadOnlyList<string> reasons) =>
        new(SubtaskPlanKind.TerminallyBlocked, reasons: reasons);
}

/// <summary>
/// Decides which subtask the loop attempts next. Static and pure: it reads only the snapshot and
/// the titles attempted in this run, never the filesystem, so the ledger stays the only interpreter
/// of the tracking files and the loop the only place that acts.
/// </summary>
/// <remarks>
/// <para>
/// An entry's dependencies are its <see cref="SubtaskState.DependsOn"/> titles, or every previous
/// entry when that is null. A dependency is met only by a <see cref="SubtaskStatus.Complete"/>
/// entry <em>earlier</em> in the index; a name that resolves to no earlier entry is never met, which
/// mirrors the ledger's rule and keeps a hand-built snapshot from scheduling a cycle.
/// </para>
/// <para>
/// Entries skipped for unmet dependencies are not "attempted": the caller adds a title to its
/// attempted set only when it starts work on it, so a dependent becomes runnable in the same run as
/// soon as its dependency completes.
/// </para>
/// </remarks>
public static class SubtaskScheduler
{
    /// <summary>Plans the next step of the subtask loop.</summary>
    /// <param name="snapshot">A freshly read snapshot.</param>
    /// <param name="attempted">The titles this run has already started work on, case-insensitive.</param>
    /// <param name="exhaustedJobs">
    /// Deferred entries whose desktop job used up its attempts in this run, each mapped to the German
    /// reason that ends the run; null for none. Such an entry no longer waits for its job.
    /// </param>
    /// <returns>Exactly one plan.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static SubtaskPlan Plan(
        SubtaskSnapshot snapshot,
        IReadOnlySet<string> attempted,
        IReadOnlyDictionary<string, string>? exhaustedJobs = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(attempted);

        var states = snapshot.States;
        var positions = Positions(states);
        var desktop = new List<string>();

        for (var position = 0; position < states.Count; position++)
        {
            var state = states[position];
            if (state.Status == SubtaskStatus.Complete || !DependenciesMet(states, positions, position))
            {
                continue;
            }

            // Checked before `attempted`: an entry that went deferred in this very run is exactly
            // the one whose desktop job the run should wait for.
            if (IsWaitingForDesktopJob(state, exhaustedJobs))
            {
                desktop.Add(state.Title);
                continue;
            }

            if (!attempted.Contains(state.Title) && IsRunnable(state))
            {
                return SubtaskPlan.Runnable(state);
            }
        }

        if (snapshot.Completed == snapshot.Total)
        {
            return SubtaskPlan.Done;
        }

        return desktop.Count > 0
            ? SubtaskPlan.WaitingForDesktopWindow(desktop)
            : SubtaskPlan.TerminallyBlocked(Reasons(states, positions, exhaustedJobs));
    }

    /// <summary>
    /// Lists the deferred entries waiting for their desktop job, in index order, whether or not some
    /// other entry is runnable - so the loop can run a job as soon as the idle window opens instead
    /// of only when nothing else is left.
    /// </summary>
    /// <param name="snapshot">A freshly read snapshot.</param>
    /// <param name="exhaustedJobs">As for <see cref="Plan"/>.</param>
    /// <returns>The titles <see cref="Plan"/> would wait for.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    public static IReadOnlyList<string> DesktopJobsWaiting(
        SubtaskSnapshot snapshot,
        IReadOnlyDictionary<string, string>? exhaustedJobs = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var states = snapshot.States;
        var positions = Positions(states);
        var waiting = new List<string>();

        for (var position = 0; position < states.Count; position++)
        {
            if (IsWaitingForDesktopJob(states[position], exhaustedJobs)
                && DependenciesMet(states, positions, position))
            {
                waiting.Add(states[position].Title);
            }
        }

        return waiting;
    }

    /// <summary>Whether an entry whose dependencies are met may be handed to a session.</summary>
    /// <param name="state">The entry.</param>
    /// <returns>
    /// True for Pending and Failed - the existing loop retries a failed entry once per run - and for
    /// Blocked and WaitingForHuman, whose new attempt is how a run notices the wait is over. True for
    /// a Deferred entry only once its desktop job has delivered (verification) or ran and failed
    /// (repair).
    /// </returns>
    private static bool IsRunnable(SubtaskState state) =>
        state.Status is SubtaskStatus.Pending
            or SubtaskStatus.Failed
            or SubtaskStatus.Blocked
            or SubtaskStatus.WaitingForHuman
        || (state.Status == SubtaskStatus.Deferred && (state.JobResultReady || state.JobNeedsRepair));

    /// <summary>Whether an entry whose dependencies are met waits for its desktop job.</summary>
    /// <param name="state">The entry.</param>
    /// <param name="exhaustedJobs">Entries whose job used up its attempts in this run.</param>
    /// <returns>
    /// True for a deferred entry whose result is not ready yet, unless its job is exhausted: that one
    /// is reported through <see cref="Reasons"/> instead, so the run ends rather than waiting forever.
    /// An entry whose job needs repair never waits: its job already ran and failed.
    /// </returns>
    private static bool IsWaitingForDesktopJob(
        SubtaskState state,
        IReadOnlyDictionary<string, string>? exhaustedJobs) =>
        state.Status == SubtaskStatus.Deferred
        && !state.JobResultReady
        && !state.JobNeedsRepair
        && exhaustedJobs?.ContainsKey(state.Title) != true;

    /// <summary>Maps each title to its first position, case-insensitively, as the ledger de-duplicates.</summary>
    private static Dictionary<string, int> Positions(IReadOnlyList<SubtaskState> states)
    {
        var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var position = 0; position < states.Count; position++)
        {
            positions.TryAdd(states[position].Title, position);
        }

        return positions;
    }

    private static bool DependenciesMet(
        IReadOnlyList<SubtaskState> states,
        Dictionary<string, int> positions,
        int position) =>
        Blocker(states, positions, position) is null;

    /// <summary>Finds the first dependency of an entry that is not met.</summary>
    /// <returns>
    /// Null when every dependency is met; otherwise the earlier entry that is not complete, or - for
    /// a name that resolves to no earlier entry - just that name.
    /// </returns>
    private static (SubtaskState? Entry, string Name)? Blocker(
        IReadOnlyList<SubtaskState> states,
        Dictionary<string, int> positions,
        int position)
    {
        var dependsOn = states[position].DependsOn;

        if (dependsOn is null)
        {
            for (var earlier = 0; earlier < position; earlier++)
            {
                if (states[earlier].Status != SubtaskStatus.Complete)
                {
                    return (states[earlier], states[earlier].Title);
                }
            }

            return null;
        }

        foreach (var name in dependsOn)
        {
            if (!positions.TryGetValue(name, out var target) || target >= position)
            {
                return (null, name);
            }

            if (states[target].Status != SubtaskStatus.Complete)
            {
                return (states[target], states[target].Title);
            }
        }

        return null;
    }

    /// <summary>Explains why nothing can run.</summary>
    /// <remarks>
    /// The dependency chains come first and alone when there are any: an entry that was attempted
    /// this run already explains itself through its status, and repeating it next to every dependent
    /// would bury the chain. Only when no entry waits on another are the attempted ones listed. An
    /// exhausted desktop job is the exception: its reason names the step and the outcome, which no
    /// status can, so it always leads the list.
    /// </remarks>
    private static List<string> Reasons(
        IReadOnlyList<SubtaskState> states,
        Dictionary<string, int> positions,
        IReadOnlyDictionary<string, string>? exhaustedJobs)
    {
        var exhausted = new List<string>();
        var chains = new List<string>();
        var remaining = new List<string>();

        for (var position = 0; position < states.Count; position++)
        {
            var state = states[position];
            if (state.Status == SubtaskStatus.Complete)
            {
                continue;
            }

            var blocker = Blocker(states, positions, position);
            if (blocker is null && exhaustedJobs is not null && exhaustedJobs.TryGetValue(state.Title, out var reason))
            {
                exhausted.Add(reason);
            }
            else if (blocker is null)
            {
                remaining.Add($"{state.Title} ({Describe(state)})");
            }
            else if (blocker.Value.Entry is null)
            {
                chains.Add($"{state.Title} hat eine ungültige Abhängigkeit '{blocker.Value.Name}'");
            }
            else
            {
                chains.Add($"{state.Title} wartet auf {blocker.Value.Name} ({Describe(blocker.Value.Entry)})");
            }
        }

        return [.. exhausted, .. chains.Count > 0 ? chains : remaining];
    }

    /// <summary>The status as it is written on disk, followed by its reason when there is one.</summary>
    private static string Describe(SubtaskState state)
    {
        // The payload's reason still says the job is waiting, so it is ignored here.
        if (state is { Status: SubtaskStatus.Deferred, JobNeedsRepair: true })
        {
            return "deferred: Desktop-Job fehlgeschlagen, keine neue Anforderung";
        }

        var status = state.Status switch
        {
            SubtaskStatus.Failed => "failed",
            SubtaskStatus.Blocked => "blocked",
            SubtaskStatus.WaitingForHuman => "waiting_for_human",
            SubtaskStatus.Deferred => "deferred",
            SubtaskStatus.Complete => "complete",
            _ => "pending",
        };

        return string.IsNullOrWhiteSpace(state.FailReason) ? status : $"{status}: {state.FailReason}";
    }
}
