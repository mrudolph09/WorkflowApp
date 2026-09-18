using Workflow.Models;

namespace Workflow.Services;

/// <summary>
/// The live, editable subtask configuration the tab owns while phases 1-3 run.
/// </summary>
/// <remarks>
/// This interface exists <em>only</em> as the capture source for
/// <see cref="SubtaskConfiguration.Capture(ISubtaskConfiguration)"/> (design issue 3 resolved).
/// Nothing downstream of phase-4 entry may hold it: the orchestrator receives the captured
/// <see cref="SubtaskConfiguration"/> record instead, which is what makes it impossible to observe
/// the enabled flag at one moment and the tracking directory at another (requirement 1.6).
/// </remarks>
public interface ISubtaskConfiguration
{
    /// <summary>Whether subtask mode is selected at this instant.</summary>
    public bool SubtasksEnabled { get; }

    /// <summary>
    /// The tracking directory selected at this instant, or null when the user has not chosen one.
    /// Blank and whitespace are expected values, not programming errors.
    /// </summary>
    public string? WorkflowDirectory { get; }
}

/// <summary>
/// One immutable snapshot of the subtask configuration, taken exactly once when phase 4 is entered.
/// </summary>
/// <param name="Enabled">Whether the run executes as subtasks. False is a normal implementation run.</param>
/// <param name="WorkflowDirectory">
/// The tracking directory as it stood at capture time. Carried through even when
/// <paramref name="Enabled"/> is false, so persistence records what the user actually selected.
/// </param>
/// <remarks>
/// <para>
/// Design "Components and Interfaces -&gt; Configuration and Persistence" (issue 3 resolved): the
/// configuration crosses the phase-4 boundary as one immutable record, not as a live object with
/// independent getters. The tab keeps the editable state; <see cref="Capture(ISubtaskConfiguration)"/>
/// reads it once and everything afterwards sees only this record, so the pairing of
/// <paramref name="Enabled"/> and <paramref name="WorkflowDirectory"/> is atomic by construction and
/// no paired-read guarantee has to be argued (requirement 1.6).
/// </para>
/// <para>
/// A <see langword="null"/> <see cref="SubtaskConfiguration"/> - the default of
/// <see cref="WorkflowRunRequest.Subtasks"/> - means disabled. <see cref="IsEnabled"/> is the single
/// place that decides this, so no caller has to repeat the null check and reach a different answer.
/// </para>
/// </remarks>
public sealed record SubtaskConfiguration(bool Enabled, string? WorkflowDirectory)
{
    /// <summary>
    /// The message used if a future validator ever reports an invalid directory without naming a
    /// reason. <see cref="WorkflowDirectoryValidation.Validate"/> always supplies one today, so this
    /// text is a guard rather than a reachable message - but it stays German, because the user is
    /// the one who would read it.
    /// </summary>
    private const string UnknownReasonMessage = "Das Workflow-Verzeichnis ist ungültig.";

    /// <summary>The snapshot that means "normal implementation run": mode off, no directory.</summary>
    public static SubtaskConfiguration Disabled { get; } = new(false, null);

    /// <summary>
    /// Whether a captured configuration - possibly absent - asks for a subtask run.
    /// </summary>
    /// <param name="configuration">The captured snapshot, or null.</param>
    /// <returns>
    /// True only for a non-null snapshot whose <see cref="Enabled"/> flag is set. A null
    /// configuration means disabled (design: "Null configuration means disabled").
    /// </returns>
    public static bool IsEnabled(SubtaskConfiguration? configuration) =>
        configuration is { Enabled: true };

    /// <summary>
    /// Takes the one snapshot of the live configuration, at phase-4 entry.
    /// </summary>
    /// <param name="source">The tab's live, editable state.</param>
    /// <returns>An immutable record of the enabled flag and the tracking directory.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <remarks>
    /// Each member of <paramref name="source"/> is read exactly once, here, and never again: this is
    /// the only method in the feature that touches <see cref="ISubtaskConfiguration"/>. Validation
    /// and every later consumer work from the returned record, so a change the user makes afterwards
    /// - or a re-read taken at a second moment - cannot reach the run.
    /// </remarks>
    public static SubtaskConfiguration Capture(ISubtaskConfiguration source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new SubtaskConfiguration(source.SubtasksEnabled, source.WorkflowDirectory);
    }

    /// <summary>
    /// Takes the phase-4 snapshot and immediately rejects it if subtask mode is enabled but its
    /// tracking directory is unusable.
    /// </summary>
    /// <param name="source">The tab's live, editable state.</param>
    /// <returns>The captured configuration, enabled and validated or simply disabled.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="SubtaskConfigurationException">
    /// Subtask mode is enabled and the directory fails <see cref="WorkflowDirectoryValidation"/>.
    /// </exception>
    /// <remarks>
    /// This is the phase-4 entry point (requirement 1.8). It validates the <em>captured</em>
    /// directory, not a second read of <paramref name="source"/>, and it never answers an
    /// enabled-but-invalid configuration with <see cref="Disabled"/>: downgrading it to a normal
    /// full-context implementation run would turn unbounded work loose on the product working
    /// directory, contradicting requirement 1.3.
    /// </remarks>
    public static SubtaskConfiguration CaptureValidated(ISubtaskConfiguration source)
    {
        var captured = Capture(source);
        captured.EnsureUsable();
        return captured;
    }

    /// <summary>
    /// Re-checks this snapshot's tracking directory and raises if an enabled configuration cannot be
    /// used.
    /// </summary>
    /// <exception cref="SubtaskConfigurationException">
    /// Subtask mode is enabled and the directory is blank, absent, or lacks the
    /// <see cref="SubtaskPaths.TemplateFolderName"/> marker. The exception carries the German message
    /// the tab presents while implementation stays recoverable.
    /// </exception>
    /// <remarks>
    /// A disabled snapshot is accepted unconditionally and touches no filesystem: its directory is
    /// irrelevant to a normal run. An enabled one is validated again here even though the start gate
    /// already checked it, because the marker can disappear between the two moments (requirement 1.8).
    /// </remarks>
    public void EnsureUsable()
    {
        if (!Enabled)
        {
            return;
        }

        var validation = WorkflowDirectoryValidation.Validate(WorkflowDirectory);
        if (validation.IsValid)
        {
            return;
        }

        throw new SubtaskConfigurationException(validation.ErrorMessage ?? UnknownReasonMessage);
    }
}
