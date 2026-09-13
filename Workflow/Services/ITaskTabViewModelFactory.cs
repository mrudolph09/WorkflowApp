using Workflow.ViewModels;

namespace Workflow.Services;

/// <summary>Builds a fully wired tab view model, one per tab.</summary>
public interface ITaskTabViewModelFactory
{
    /// <summary>Creates a new tab.</summary>
    /// <returns>The tab view model.</returns>
    public TaskTabViewModel Create();
}
