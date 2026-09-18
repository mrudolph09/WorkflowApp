using System.Collections.ObjectModel;

namespace Workflow.Models;

/// <summary>
/// The outcome one subtask's tracking files record. The vocabulary is deliberately closed on the two
/// decisive values: <c>complete</c> and <c>failed</c> (compared case-insensitively) decide, and every
/// other value on disk - including <c>pending</c> and anything unrecognised - means "not done yet"
/// rather than "broken" (design issue 1).
/// </summary>
public enum SubtaskStatus
{
    /// <summary>
    /// Not finished. The default, so a freshly decomposed index reports zero failures and requirement
    /// 3.3's <c>{K} fehlgeschlagen</c> appears only for real failures.
    /// </summary>
    Pending,

    /// <summary>The session published <c>status: complete</c>. The only state that counts towards green.</summary>
    Complete,

    /// <summary>
    /// The session published <c>status: failed</c>, or the entry is unusable - an unsafe title, a
    /// missing description, or a status payload that stayed unreadable after the settling retries.
    /// </summary>
    Failed,
}

/// <summary>Where the subtask run currently stands, which selects the indicator's text and colour.</summary>
public enum SubtaskStage
{
    /// <summary>Nothing has started; the indicator is grey.</summary>
    Idle,

    /// <summary>The decomposition session is running; the indicator shows <c>Zerlegung läuft…</c>.</summary>
    Decomposing,

    /// <summary>The ordered loop is running; the indicator shows <c>{N} von {M}</c>.</summary>
    Running,

    /// <summary>The run ended, successfully or with failures.</summary>
    Finished,
}

/// <summary>One subtask's state, as read from its tracking files.</summary>
/// <param name="Title">The subtask title exactly as the ordered index lists it; also its folder name.</param>
/// <param name="Status">The outcome the evidence on disk establishes.</param>
/// <param name="FailReason">
/// The <c>failreason</c> from the status payload, or the reason the entry itself is unusable. Null
/// whenever there is nothing to explain - never an empty string standing in for "no reason".
/// Requirement 3.7 carries this value as far as the indicator tooltip.
/// </param>
public sealed record SubtaskState(string Title, SubtaskStatus Status, string? FailReason = null);

/// <summary>
/// Everything the tracking files say about one task's subtasks: the ordered states plus the counts
/// the loop, the journal and the indicator all agree on.
/// </summary>
/// <remarks>
/// <para>
/// The counts are not supplied by the caller; they are derived from <see cref="States"/> at
/// construction, over a private copy of the list. That is what makes it impossible for a snapshot to
/// report a total that disagrees with the states it carries - the disagreement requirement 2.6 and
/// the <c>{N} von {M}</c> display depend on never happening.
/// </para>
/// <para>
/// Note for consumers: this record's generated equality compares <see cref="States"/> by reference,
/// so two snapshots read from identical files are never equal. Do not compare snapshots to suppress
/// redundant UI updates - compare the counts, or the states element by element.
/// </para>
/// </remarks>
public sealed record SubtaskSnapshot
{
    /// <summary>Creates the snapshot and derives its counts.</summary>
    /// <param name="states">
    /// The states in index order, which is execution order. Copied, so a caller that keeps a mutable
    /// reference cannot drive the counts and the list apart afterwards.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="states"/> is null.</exception>
    public SubtaskSnapshot(IReadOnlyList<SubtaskState> states)
    {
        ArgumentNullException.ThrowIfNull(states);

        var ordered = new ReadOnlyCollection<SubtaskState>([.. states]);
        States = ordered;

        var completed = 0;
        var failed = 0;
        foreach (var state in ordered)
        {
            if (state.Status == SubtaskStatus.Complete)
            {
                completed++;
            }
            else if (state.Status == SubtaskStatus.Failed)
            {
                failed++;
            }
        }

        Total = ordered.Count;
        Completed = completed;
        Failed = failed;
    }

    /// <summary>The states in index order. Exposed read-only; nothing outside may add or remove entries.</summary>
    public IReadOnlyList<SubtaskState> States { get; }

    /// <summary>Number of states, which is the number of unique titles the index listed.</summary>
    public int Total { get; }

    /// <summary>Number of <see cref="SubtaskStatus.Complete"/> states.</summary>
    public int Completed { get; }

    /// <summary>Number of <see cref="SubtaskStatus.Failed"/> states. Pending entries never count here.</summary>
    public int Failed { get; }
}

/// <summary>
/// One progress report from the subtask run to the UI, delivered through
/// <see cref="IProgress{T}"/>.
/// </summary>
/// <param name="Stage">Where the run stands.</param>
/// <param name="Completed">Number of complete subtasks; the <c>N</c> of <c>{N} von {M}</c>.</param>
/// <param name="Total">Number of subtasks in the index; the <c>M</c> of <c>{N} von {M}</c>.</param>
/// <param name="Failed">Number of failed subtasks; drives <c>{K} fehlgeschlagen</c>.</param>
/// <param name="CurrentTitle">The subtask being attempted, or null while none is.</param>
/// <param name="States">
/// The ordered states. Requirement 3.7 (design issue 7): the failure reasons reach the tooltip in
/// this list rather than in a separate channel, so live progress and progress restored on recovery
/// share one shape. Exposed as a read-only list, honouring the convention against public mutable
/// list types.
/// </param>
public sealed record SubtaskProgress(
    SubtaskStage Stage,
    int Completed,
    int Total,
    int Failed,
    string? CurrentTitle,
    IReadOnlyList<SubtaskState> States)
{
    /// <inheritdoc cref="SubtaskProgress"/>
    public IReadOnlyList<SubtaskState> States { get; } = States ?? throw new ArgumentNullException(nameof(States));
}
