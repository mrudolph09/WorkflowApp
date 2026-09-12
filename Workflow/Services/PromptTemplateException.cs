namespace Workflow.Services;

/// <summary>Raised when a prompt template is missing, empty, or contains an unknown token.</summary>
public sealed class PromptTemplateException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PromptTemplateException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">Description of the problem, naming the file.</param>
    public PromptTemplateException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    /// <param name="message">Description of the problem, naming the file.</param>
    /// <param name="innerException">The underlying failure.</param>
    public PromptTemplateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
