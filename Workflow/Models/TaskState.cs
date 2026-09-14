using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace Workflow.Models;

/// <summary>One phase's recorded outcome inside the workflow journal.</summary>
/// <param name="Phase">The phase this entry describes.</param>
/// <param name="Status">The phase's last recorded status.</param>
/// <param name="CompletedUtc">When the phase finished, or null while it has not.</param>
public sealed record TaskPhaseState(WorkflowPhase Phase, PhaseStatus Status, DateTimeOffset? CompletedUtc);

/// <summary>
/// The workflow journal: what the application knows about one task's progress through the four
/// phases. Written to <see cref="TaskPaths.StateAbsolute"/>.
/// </summary>
/// <remarks>
/// It deliberately stores neither the task name nor the working directory. Both are derived from
/// the folder the journal sits in and that folder's parent, so renaming or moving a task folder -
/// including through <c>TaskFolderService.Rename</c>, which moves the whole directory - stays
/// correct without any migration.
/// </remarks>
[SuppressMessage(
    "Usage",
    "CA2227:Collection properties should be read only",
    Justification = "System.Text.Json requires a settable collection property to populate this list.")]
public sealed class TaskState
{
    /// <summary>The schema version this build writes and is able to read.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Schema version of the file on disk.</summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>The Taskbeschreibung, verbatim. The only field that cannot be derived.</summary>
    public string TaskDescription { get; set; } = string.Empty;

    /// <summary>When the journal was first written.</summary>
    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>When the journal was last written. Drives the recovery age window.</summary>
    public DateTimeOffset UpdatedUtc { get; set; }

    /// <summary>True when the user closed the recovered tab instead of continuing it.</summary>
    public bool Dismissed { get; set; }

    /// <summary>The four phases, always in <see cref="PhaseCatalog"/> order.</summary>
    public Collection<TaskPhaseState> Phases { get; set; } = [];
}
