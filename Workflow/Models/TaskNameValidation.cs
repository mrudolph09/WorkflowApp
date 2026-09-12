namespace Workflow.Models;

/// <summary>Result of validating a task name.</summary>
/// <param name="IsValid">True when the name can be used as a folder name.</param>
/// <param name="ErrorMessage">German message to show the user, or null when valid.</param>
public sealed record TaskNameValidation(bool IsValid, string? ErrorMessage)
{
    /// <summary>The successful result.</summary>
    public static TaskNameValidation Ok { get; } = new(true, null);

    /// <summary>Creates a failed result.</summary>
    /// <param name="message">German message to show the user.</param>
    /// <returns>A failed validation result.</returns>
    public static TaskNameValidation Error(string message) => new(false, message);
}
