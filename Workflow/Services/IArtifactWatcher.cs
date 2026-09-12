namespace Workflow.Services;

/// <summary>Signals when a phase's artefact condition is satisfied.</summary>
public interface IArtifactWatcher : IDisposable
{
    /// <summary>Waits until the condition holds.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when the artefacts satisfy the rule.</returns>
    public Task WaitAsync(CancellationToken cancellationToken);
}
