namespace Workflow.Services;

/// <summary>Raised when an artefact watcher can no longer observe its directory.</summary>
public sealed class ArtifactWatchException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ArtifactWatchException()
    {
    }

    /// <summary>Creates the exception with a German message naming the directory.</summary>
    /// <param name="message">The message shown to the user.</param>
    public ArtifactWatchException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    /// <param name="message">The message shown to the user.</param>
    /// <param name="innerException">The underlying failure.</param>
    public ArtifactWatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
