namespace Workflow.Services;

/// <summary>
/// A desktop job step could not be started safely - for example, its process could not be contained
/// in the job object - and was torn down again. The message is the German reason the runner records
/// with <see cref="Models.DesktopJobOutcome.StartFailed"/>.
/// </summary>
public sealed class DesktopJobStartException : Exception
{
    /// <summary>Creates the exception.</summary>
    public DesktopJobStartException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The German reason.</param>
    public DesktopJobStartException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    /// <param name="message">The German reason.</param>
    /// <param name="innerException">The underlying failure.</param>
    public DesktopJobStartException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
