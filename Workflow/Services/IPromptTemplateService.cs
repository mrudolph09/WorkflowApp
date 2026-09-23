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

    /// <summary>Checks every shipped template at application start.</summary>
    /// <remarks>
    /// Requirement 6.3 and 6.4: every <c>*.md</c> file present in the prompt directory is checked
    /// against its own entry in <see cref="PromptTemplateCatalog.AllowedTokens"/> - not against the
    /// union of all known token names - and a file the catalog does not recognize is reported
    /// rather than skipped. A catalog entry with no file on disk is not reported here; that is the
    /// "not found" case <see cref="Render"/> raises at the point of use.
    /// </remarks>
    /// <returns>German error messages; an empty list means all templates are usable.</returns>
    public IReadOnlyList<string> ValidateAll();
}
