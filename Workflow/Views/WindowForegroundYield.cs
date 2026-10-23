using System.Windows;
using System.Windows.Threading;
using Workflow.Services;

namespace Workflow.Views;

/// <summary>
/// <see cref="IForegroundYield"/> for the main window: minimises it on the UI dispatcher before a
/// desktop job step and puts it back to its previous state afterwards.
/// </summary>
/// <param name="dispatcher">The UI dispatcher that owns the window.</param>
/// <param name="window">Resolves the main window at call time; null when there is none.</param>
public sealed class WindowForegroundYield(Dispatcher dispatcher, Func<Window?> window) : IForegroundYield
{
    // Written and read only on the dispatcher thread.
    private WindowState? _previous;

    /// <inheritdoc />
    public Task MinimiseAsync() =>
        dispatcher.InvokeAsync(() =>
        {
            if (window() is not { } target || target.WindowState == WindowState.Minimized)
            {
                _previous = null;
                return;
            }

            _previous = target.WindowState;
            target.WindowState = WindowState.Minimized;
        }).Task;

    /// <inheritdoc />
    public Task RestoreAsync() =>
        dispatcher.InvokeAsync(() =>
        {
            // Only a window this yield minimised is restored; one the user minimised stays as it is.
            if (_previous is { } state && window() is { } target)
            {
                target.WindowState = state;
            }

            _previous = null;
        }).Task;
}
