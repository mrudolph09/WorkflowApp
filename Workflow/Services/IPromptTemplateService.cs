namespace Workflow.Services;

/// <summary>Loads prompt templates and substitutes their variables.</summary>
public interface IPromptTemplateService
{
    /// <summary>Renders one template.</summary>
    /// <param name="fileName">File name inside the prompt directory, for example "initial_prompt.md".</param>
    /// <param name="variables">Values keyed by token name without braces.</param>
    /// <returns>The rendered prompt text.</returns>
    /// <exception cref="PromptTemplateException">
    /// The file is missing or empty, or a token remained unresolved.
    /// </exception>
    public string Render(string fileName, IReadOnlyDictionary<string, string> variables);

    /// <summary>Checks every phase template at application start.</summary>
    /// <returns>German error messages; an empty list means all templates are usable.</returns>
    public IReadOnlyList<string> ValidateAll();
}
