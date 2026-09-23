using Workflow.Models;

namespace Workflow.Services;

/// <summary>
/// Maps every prompt file the <c>Prompt\**\*.md</c> content glob ships to the token set that
/// <em>that</em> file may use.
/// </summary>
/// <remarks>
/// <para>
/// Requirement 6.3 and 6.4: startup validation checks each template against its own set. A union
/// of all known names must never replace the per-file check - it would accept an execution-only
/// token such as <c>{subtask}</c> inside a phase prompt, which is precisely the defect the
/// per-file catalog exists to catch. <see cref="PromptVariables"/> therefore deliberately exposes
/// no union property.
/// </para>
/// <para>
/// The catalog is defined over the <em>shipped</em> set, not over <see cref="PhaseCatalog"/> alone:
/// a six-entry catalog would leave <c>counter_prompt.md</c> and <c>evidence_gate.md</c>
/// permanently unvalidated. Both are presently unreferenced by code but are shipped by the glob,
/// and both use <c>{tasktitel}</c> - a decomposition-set token. They are mapped to
/// <see cref="PromptVariables.SubtaskCreationNames"/>, the narrowest layer that admits that token;
/// widening the base set to accommodate them would silently legalise <c>{tasktitel}</c> in the
/// four phase prompts as well.
/// </para>
/// <para>
/// The four phase entries are derived from <see cref="PhaseCatalog.All"/> rather than repeated, so
/// a newly added phase cannot ship an uncatalogued prompt file.
/// </para>
/// </remarks>
public static class PromptTemplateCatalog
{
    /// <summary>File name inside the prompt directory to the tokens that file may use.</summary>
    /// <remarks>
    /// Keyed case-insensitively because the prompt directory lives on a Windows file system, where
    /// <c>Run_Subtask.md</c> and <c>run_subtask.md</c> are the same file.
    /// </remarks>
    public static IReadOnlyDictionary<string, IReadOnlyCollection<string>> AllowedTokens { get; } =
        Build();

    private static Dictionary<string, IReadOnlyCollection<string>> Build()
    {
        var catalog = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in PhaseCatalog.All)
        {
            catalog[definition.PromptFile] = PromptVariables.KnownNames;
        }

        catalog["create_subtasks.md"] = PromptVariables.SubtaskCreationNames;
        catalog["counter_prompt.md"] = PromptVariables.SubtaskCreationNames;
        catalog["evidence_gate.md"] = PromptVariables.SubtaskCreationNames;
        catalog["run_subtask.md"] = PromptVariables.SubtaskRunNames;

        return catalog;
    }
}
