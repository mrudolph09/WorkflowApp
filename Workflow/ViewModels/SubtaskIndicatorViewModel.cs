using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Workflow.Models;

namespace Workflow.ViewModels;

/// <summary>
/// The second grey/yellow/green indicator, shown beside the phase indicators while subtask mode is
/// selected (requirement 3.1). It turns one <see cref="SubtaskProgress"/> payload into the label,
/// the counts, the failure text and the tooltip the view binds to.
/// </summary>
/// <remarks>
/// <para>
/// The colour is derived from the counts alone. Green is <c>Total &gt; 0 &amp;&amp; Completed ==
/// Total</c> (requirement 3.4, design issue 6a); the absence of failures never turns it green, and
/// a task completed manually keeps showing its real <c>{N} von {M}</c> instead of claiming green
/// (design issue 6b) because nothing outside the counts can reach the colour.
/// </para>
/// <para>
/// <see cref="SubtaskStage.Finished"/>: the design names four stages but nothing in production code
/// publishes this one yet. The indicator therefore treats it exactly like
/// <see cref="SubtaskStage.Running"/> - it shows the counts and is judged by them, so a finished run
/// with outstanding or failed work stays yellow (requirement 3.6) and a finished run whose entries
/// are all complete is already green by the rule above (requirement 3.5). The stage needs no branch
/// of its own: the only colour distinction requirement 3.4 draws besides green is grey before phase
/// 4, and a finished run is not before phase 4.
/// </para>
/// </remarks>
public sealed partial class SubtaskIndicatorViewModel : ObservableObject
{
    private const string DecompositionMessage = "Zerlegung läuft…";

    /// <summary>Drives the icon and its colour through the existing phase-status converters.</summary>
    [ObservableProperty]
    private PhaseStatus _status = PhaseStatus.Pending;

    /// <summary>The decomposition message, or <c>{N} von {M}</c>.</summary>
    [ObservableProperty]
    private string _progressText = Counts(0, 0);

    /// <summary><c>{K} fehlgeschlagen</c>, or empty when nothing failed.</summary>
    [ObservableProperty]
    private string _failedText = string.Empty;

    /// <summary>Whether the failure text is shown; bound through the visibility converter.</summary>
    [ObservableProperty]
    private bool _hasFailures;

    /// <summary>Title and reason of every failed entry, or null when there is nothing to explain.</summary>
    [ObservableProperty]
    private string? _failureTooltip;

    /// <summary>The label requirement 3.1 fixes, distinguishing this indicator from the phase ones.</summary>
    public string DisplayName { get; } = "Subtasks";

    /// <summary>
    /// Applies one progress payload: the stage selects the text, the counts select the colour and
    /// the failure line, and the ordered states supply the tooltip (requirement 3.7, design issue 7).
    /// </summary>
    /// <param name="progress">The payload published by the run, or seeded from the ledger on recovery.</param>
    /// <exception cref="ArgumentNullException"><paramref name="progress"/> is null.</exception>
    public void Apply(SubtaskProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        ProgressText = progress.Stage == SubtaskStage.Decomposing
            ? DecompositionMessage
            : Counts(progress.Completed, progress.Total);

        Status = Colour(progress);

        HasFailures = progress.Failed > 0;
        FailedText = progress.Failed > 0
            ? string.Format(CultureInfo.CurrentCulture, "{0} fehlgeschlagen", progress.Failed)
            : string.Empty;

        FailureTooltip = Tooltip(progress.States);
    }

    /// <summary>
    /// Returns the indicator to the state it has before anything is known - grey, no counts, no
    /// failures - so a tab that starts over cannot keep showing the previous run's numbers.
    /// </summary>
    public void Reset()
    {
        Status = PhaseStatus.Pending;
        ProgressText = Counts(0, 0);
        FailedText = string.Empty;
        HasFailures = false;
        FailureTooltip = null;
    }

    private static string Counts(int completed, int total) =>
        string.Format(CultureInfo.CurrentCulture, "{0} von {1}", completed, total);

    private static PhaseStatus Colour(SubtaskProgress progress)
    {
        if (progress.Total > 0 && progress.Completed == progress.Total)
        {
            return PhaseStatus.Completed;
        }

        return progress.Stage == SubtaskStage.Idle ? PhaseStatus.Pending : PhaseStatus.Active;
    }

    private static string? Tooltip(IReadOnlyList<SubtaskState> states)
    {
        List<string>? lines = null;

        foreach (var state in states)
        {
            // Requirement 3.3: only a Failed entry is a failure. Pending - including every
            // unrecognised status the ledger mapped to Pending - is work still to do, not a fault,
            // so it must never appear here.
            if (state.Status != SubtaskStatus.Failed)
            {
                continue;
            }

            lines ??= [];
            lines.Add(string.IsNullOrWhiteSpace(state.FailReason)
                ? state.Title
                : string.Concat(state.Title, ": ", state.FailReason));
        }

        return lines is null ? null : string.Join(Environment.NewLine, lines);
    }
}
