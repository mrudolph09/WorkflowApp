using Workflow.Models;

namespace Workflow.Services;

/// <summary>
/// The live, editable subtask configuration the tab owns while phases 1-3 run.
/// </summary>
/// <remarks>
/// This interface exists <em>only</em> as the capture source for
/// <see cref="SubtaskConfiguration.Capture(ISubtaskConfiguration)"/> (design issue 3 resolved),
/// reached either directly or through the deferred form
/// <see cref="SubtaskConfiguration.Deferred(ISubtaskConfiguration)"/> the run carries.
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
    /// Hands the run a capture that has not been taken yet, so that <see cref="Capture"/> runs when
    /// the run first needs the answer - phase-4 entry - rather than when the run is built.
    /// </summary>
    /// <param name="source">The tab's live, editable state.</param>
    /// <returns>
    /// A once-only deferred capture. Forcing it the first time reads
    /// <paramref name="source"/>; every later force returns that same immutable record.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// Requirement 1.6 puts the snapshot at <em>phase-4 entry</em>, not at run entry: while phases
    /// 1-3 run the user may still tick the checkbox or pick a different directory, and those edits
    /// must reach the run. Capturing when the request is built would freeze the configuration
    /// before the user was finished with it. Deferring the call is what closes that gap, and the
    /// phase-4 guard in <c>WorkflowOrchestrator.RunPhaseAsync</c> - whose <c>&amp;&amp;</c>
    /// short-circuits on the phase check - is what keeps a run that never reaches implementation
    /// from reading the tab at all.
    /// </para>
    /// <para>
    /// <see cref="Lazy{T}"/> rather than a bare delegate, and this is the load-bearing part: the
    /// orchestrator touches the member twice, once in that guard and once to compose the tracking
    /// paths, so a delegate would re-read the tab and could observe the enabled flag and the
    /// directory from two different moments - exactly the defect the snapshot exists to prevent
    /// (task 2.3). The default <see cref="Lazy{T}"/> factory constructor uses
    /// <see cref="System.Threading.LazyThreadSafetyMode.ExecutionAndPublication"/>, so "captured at
    /// most once" is a property of the type rather than of how carefully callers use it.
    /// </para>
    /// <para>
    /// The null check runs here, where the request is built, and not inside the deferred factory:
    /// the factory is invoked deep inside a run nobody awaits at the call site, so a programming
    /// error must fail while the stack still names the caller.
    /// </para>
    /// <para>
    /// <strong>Which thread reads the tab.</strong> In the application the factory runs on the WPF
    /// dispatcher thread, exactly as the previous capture at run entry did, so no marshalling is
    /// added and none is needed. The run is started from a <c>[RelayCommand]</c> on the UI thread
    /// and <see cref="WorkflowOrchestrator"/> itself introduces no hop: that file contains no
    /// <c>ConfigureAwait(false)</c> and no <c>Task.Run</c>. Note the scope - only the
    /// <c>ConfigureAwait</c> half is a repository-wide rule (<c>Directory.Build.props</c> suppresses
    /// CA2007 for it); <c>Task.Run</c> <em>is</em> used elsewhere in the app, in
    /// <c>ArtifactWatcher</c>, <c>TaskRecoveryScanner</c> and <c>ConPtySession</c>. Those pool-thread
    /// completions are harmless here, because an awaiter with a captured
    /// <c>DispatcherSynchronizationContext</c> posts its continuation back to the dispatcher no
    /// matter which thread completed the task. So every continuation, including the phase-4 guard
    /// that forces this capture, resumes on the captured dispatcher context. There is therefore no
    /// thread affinity to violate (these are plain CLR properties, not <c>DependencyObject</c>
    /// state) and no cross-thread visibility question about the non-volatile fields
    /// <c>[ObservableProperty]</c> generates.
    /// Marshalling through the dispatcher would in fact be the riskier choice:
    /// <see cref="Lazy{T}"/> holds its monitor while the factory runs, so a blocking
    /// <c>Dispatcher.Invoke</c> from inside it would deadlock the run against a busy UI thread.
    /// If that await discipline is ever relaxed for this orchestrator, revisit this paragraph -
    /// the capture would then be taken on a pool thread.
    /// </para>
    /// </remarks>
    public static Lazy<SubtaskConfiguration> Deferred(ISubtaskConfiguration source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new Lazy<SubtaskConfiguration>(() => Capture(source));
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
