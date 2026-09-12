using Workflow.Models;

namespace Workflow.Services;

/// <summary>Creates one artefact watcher per phase.</summary>
public interface IArtifactWatcherFactory
{
    /// <summary>Creates a watcher.</summary>
    /// <param name="rule">Completion rule to apply.</param>
    /// <param name="directory">Directory to watch.</param>
    /// <param name="absolutePaths">Artefact paths the rule applies to.</param>
    /// <param name="debounce">Delay applied after a change notification before re-checking.</param>
    /// <param name="pollInterval">Fallback poll interval for dropped file-system events.</param>
    /// <returns>A watcher that has already captured its baseline.</returns>
    public IArtifactWatcher Create(
        CompletionRule rule,
        string directory,
        IReadOnlyList<string> absolutePaths,
        TimeSpan debounce,
        TimeSpan pollInterval);
}

/// <inheritdoc cref="IArtifactWatcherFactory" />
public sealed class ArtifactWatcherFactory : IArtifactWatcherFactory
{
    /// <inheritdoc />
    public IArtifactWatcher Create(
        CompletionRule rule,
        string directory,
        IReadOnlyList<string> absolutePaths,
        TimeSpan debounce,
        TimeSpan pollInterval) =>
        new ArtifactWatcher(rule, directory, absolutePaths, debounce, pollInterval);
}
