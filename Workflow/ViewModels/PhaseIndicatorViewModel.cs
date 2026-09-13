using CommunityToolkit.Mvvm.ComponentModel;
using Workflow.Models;

namespace Workflow.ViewModels;

/// <summary>One of the four grey/yellow/green station indicators.</summary>
public sealed partial class PhaseIndicatorViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    private PhaseStatus _status = PhaseStatus.Pending;

    /// <summary>Creates the indicator from a phase definition.</summary>
    /// <param name="definition">The phase this indicator represents.</param>
    public PhaseIndicatorViewModel(PhaseDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        Phase = definition.Phase;
        DisplayName = definition.DisplayName;
    }

    /// <summary>The phase this indicator represents.</summary>
    public WorkflowPhase Phase { get; }

    /// <summary>German label shown under the icon.</summary>
    public string DisplayName { get; }

    /// <summary>True while this phase is running; shows the 'Phase abschliessen' button.</summary>
    public bool IsActive => Status == PhaseStatus.Active;
}
