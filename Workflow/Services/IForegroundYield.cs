namespace Workflow.Services;

/// <summary>
/// Gets the application's own window out of the way of a desktop job's UI automation, and back
/// afterwards. Implemented in the view layer; called only while the job holds the execution gate.
/// </summary>
public interface IForegroundYield
{
    /// <summary>Minimises the main window.</summary>
    /// <returns>A task that completes once the window is minimised.</returns>
    public Task MinimiseAsync();

    /// <summary>Restores the main window to the state it had before <see cref="MinimiseAsync"/>.</summary>
    /// <returns>A task that completes once the window is restored.</returns>
    public Task RestoreAsync();
}
