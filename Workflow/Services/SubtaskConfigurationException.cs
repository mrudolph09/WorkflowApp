namespace Workflow.Services;

/// <summary>
/// Raised when the subtask configuration cannot be used: an enabled configuration whose tracking
/// directory fails validation at phase-4 entry, or a decomposition that produced an unusable index.
/// </summary>
/// <remarks>
/// A user-fixable condition, not a defect: the tab catches it, presents
/// <see cref="ValidationMessage"/>, and leaves implementation recoverable (requirements 1.8 and 2.9,
/// design "Error Handling"). It is deliberately its own type so that the phase-4 branch can let it
/// travel past the generic failure handling and never be mistaken for a reason to fall back to a
/// normal implementation run.
/// </remarks>
public sealed class SubtaskConfigurationException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SubtaskConfigurationException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The German explanation shown to the user.</param>
    public SubtaskConfigurationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    /// <param name="message">The German explanation shown to the user.</param>
    /// <param name="innerException">The underlying failure.</param>
    public SubtaskConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// The German message the tab presents. It is <see cref="Exception.Message"/> itself rather than
    /// a second, separately assigned field, so an instance carrying no user-facing text cannot exist.
    /// </summary>
    public string ValidationMessage => Message;
}
