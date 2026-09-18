using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace Workflow.Models;

/// <summary>Persisted application settings.</summary>
[SuppressMessage(
    "Usage",
    "CA2227:Collection properties should be read only",
    Justification = "System.Text.Json requires a settable collection property to populate this list.")]
public sealed class AppSettings
{
    /// <summary>Working directories the user has picked, most recent first.</summary>
    /// <remarks>
    /// <c>Collection&lt;string&gt;</c> rather than <c>List&lt;string&gt;</c>: CA1002 turns a
    /// public <c>List&lt;T&gt;</c> into a build error under AnalysisMode=All, and
    /// System.Text.Json round-trips <c>Collection&lt;T&gt;</c> without any converter.
    /// </remarks>
    public Collection<string> RecentDirectories { get; set; } = [];

    /// <summary>The directory preselected for a new tab, or null.</summary>
    public string? LastDirectory { get; set; }

    /// <summary>Tracking directories the user has picked for subtask mode, most recent first.</summary>
    /// <remarks>
    /// Kept separate from <see cref="RecentDirectories" />: the product working directory and the
    /// tracking repository are independent choices. Same <c>Collection&lt;string&gt;</c> rationale
    /// as <see cref="RecentDirectories" />. Absent in settings files written before subtask mode,
    /// where it defaults to an empty list.
    /// </remarks>
    public Collection<string> RecentWorkflowDirectories { get; set; } = [];

    /// <summary>The tracking directory preselected for subtask mode on a new tab, or null.</summary>
    public string? LastWorkflowDirectory { get; set; }
}
