using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Workflow.Models;

namespace Workflow.Services;

/// <summary>
/// The tokens a prompt template may use, in three layers: the base set the four phase prompts
/// share, the decomposition set that extends it with the tracking-repository paths, and the
/// execution set that extends that with the per-subtask title and body.
/// </summary>
/// <remarks>
/// <para>
/// Each layer is built from the one below it, both for the name sets and for the dictionaries, so
/// a token is constructed in exactly one place and the lower layers can never drift.
/// </para>
/// <para>
/// Requirement 6.6: every path token this type adds is absolute, taken verbatim from
/// <see cref="SubtaskPaths"/>. No prompt composes a root with a relative fragment, and no prompt
/// re-derives a path the application already supplies. The base tokens keep their existing
/// working-directory-relative artefact paths unchanged - the four existing phase prompts must
/// render exactly as before.
/// </para>
/// </remarks>
public static class PromptVariables
{
    /// <summary>Token names of the base set, without braces; used by the four phase prompts.</summary>
    public static IReadOnlyCollection<string> KnownNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "taskbezeichnung",
        "taskbeschreibung",
        "AppDirectory",
        "spec_path",
        "plan_path",
        "review_path",
        "done_path",
    };

    /// <summary>
    /// Token names the decomposition prompt may use: <see cref="KnownNames"/> plus the tracking
    /// root, the task folder, the subtasks folder and the task-title alias.
    /// </summary>
    public static IReadOnlyCollection<string> SubtaskCreationNames { get; } =
        new HashSet<string>(KnownNames, StringComparer.Ordinal)
        {
            "workflow_path",
            "tasktitel",
            "task_path",
            "subtask_path",
        };

    /// <summary>
    /// Token names the execution prompt may use: <see cref="SubtaskCreationNames"/> plus the title
    /// and the full description text of the subtask currently being run.
    /// </summary>
    public static IReadOnlyCollection<string> SubtaskRunNames { get; } =
        new HashSet<string>(SubtaskCreationNames, StringComparer.Ordinal)
        {
            "subtask_title",
            "subtask",
        };

    /// <summary>Builds the base substitution dictionary for one task.</summary>
    /// <param name="paths">The task's path set.</param>
    /// <param name="taskDescription">The plain-text task description.</param>
    /// <returns>A dictionary covering every name in <see cref="KnownNames"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is null.</exception>
    public static Dictionary<string, string> For(TaskPaths paths, string taskDescription)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["taskbezeichnung"] = paths.TaskName,
            ["taskbeschreibung"] = taskDescription ?? string.Empty,
            ["AppDirectory"] = paths.WorkingDirectory,
            ["spec_path"] = paths.SpecRelative,
            ["plan_path"] = paths.PlanRelative,
            ["review_path"] = paths.ReviewRelative,
            ["done_path"] = paths.DoneRelative,
        };
    }

    /// <summary>Extends <see cref="For"/> with the tracking-repository tokens of the decomposition prompt.</summary>
    /// <param name="paths">The task's working-directory path set.</param>
    /// <param name="taskDescription">The plain-text task description.</param>
    /// <param name="trackingPaths">The task's tracking-repository path set, supplying absolute paths.</param>
    /// <returns>A dictionary covering every name in <see cref="SubtaskCreationNames"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> or <paramref name="trackingPaths"/> is null.</exception>
    /// <remarks>
    /// <c>tasktitel</c> is taken from the already-built <c>taskbezeichnung</c> entry rather than
    /// recomputed, so the alias can never disagree with the token it aliases, and
    /// <c>taskbezeichnung</c> itself stays in place.
    /// </remarks>
    public static Dictionary<string, string> ForSubtaskCreation(
        TaskPaths paths,
        string taskDescription,
        SubtaskPaths trackingPaths)
    {
        ArgumentNullException.ThrowIfNull(trackingPaths);

        var variables = For(paths, taskDescription);

        variables["workflow_path"] = trackingPaths.WorkflowDirectory;
        variables["tasktitel"] = variables["taskbezeichnung"];
        variables["task_path"] = trackingPaths.TaskDirectory;
        variables["subtask_path"] = trackingPaths.SubtaskPathToken;

        return variables;
    }

    /// <summary>Extends <see cref="ForSubtaskCreation"/> with the tokens of one subtask execution.</summary>
    /// <param name="paths">The task's working-directory path set.</param>
    /// <param name="taskDescription">The plain-text task description.</param>
    /// <param name="trackingPaths">The task's tracking-repository path set, supplying absolute paths.</param>
    /// <param name="subtaskTitle">Title of the subtask being run; a single, safe path segment.</param>
    /// <param name="subtaskBody">Full text of that subtask's <c>subtask.md</c> description.</param>
    /// <returns>A dictionary covering every name in <see cref="SubtaskRunNames"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> or <paramref name="trackingPaths"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="subtaskTitle"/> is null, empty or whitespace.</exception>
    public static Dictionary<string, string> ForSubtaskRun(
        TaskPaths paths,
        string taskDescription,
        SubtaskPaths trackingPaths,
        string subtaskTitle,
        string subtaskBody)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subtaskTitle);

        var variables = ForSubtaskCreation(paths, taskDescription, trackingPaths);

        variables["subtask_title"] = subtaskTitle;
        variables["subtask"] = subtaskBody ?? string.Empty;

        return variables;
    }
}

/// <inheritdoc cref="IPromptTemplateService" />
public sealed partial class PromptTemplateService : IPromptTemplateService
{
    private readonly string _promptDirectory;

    /// <summary>Creates the service.</summary>
    /// <param name="promptDirectory">Directory holding the *.md templates.</param>
    public PromptTemplateService(string promptDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptDirectory);

        _promptDirectory = promptDirectory;
    }

    [GeneratedRegex(@"\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    /// <inheritdoc />
    public string Render(string fileName, IReadOnlyDictionary<string, string> variables)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(variables);

        var text = ReadTemplate(fileName);

        var unresolved = new List<string>();
        var rendered = TokenRegex().Replace(text, match =>
        {
            var name = match.Groups["name"].Value;
            if (variables.TryGetValue(name, out var value))
            {
                return value;
            }

            unresolved.Add(name);
            return match.Value;
        });

        if (unresolved.Count > 0)
        {
            var names = string.Join(", ", unresolved.Distinct(StringComparer.Ordinal).Select(n => "{" + n + "}"));
            throw new PromptTemplateException(
                $"Die Prompt-Datei '{fileName}' enthält unbekannte Platzhalter: {names}.");
        }

        return rendered;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ValidateAll()
    {
        var errors = new List<string>();

        foreach (var fileName in ShippedTemplateFiles())
        {
            if (!PromptTemplateCatalog.AllowedTokens.TryGetValue(fileName, out var allowed))
            {
                // Requirement 6.3: a shipped file the catalog does not recognize is itself an
                // error. Skipping it silently is how counter_prompt.md and evidence_gate.md went
                // unvalidated in the first place.
                errors.Add(
                    $"Die Prompt-Datei '{fileName}' ist dem Prompt-Katalog nicht bekannt und kann nicht geprüft werden.");
                continue;
            }

            try
            {
                var text = ReadTemplate(fileName);

                // Requirement 6.4: checked against THIS file's set, never against the union of all
                // known names - the union would accept {subtask} inside a phase prompt.
                var unknown = TokenRegex()
                    .Matches(text)
                    .Select(m => m.Groups["name"].Value)
                    .Where(n => !allowed.Contains(n))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (unknown.Count > 0)
                {
                    var names = string.Join(", ", unknown.Select(n => "{" + n + "}"));
                    errors.Add($"Die Prompt-Datei '{fileName}' enthält unbekannte Platzhalter: {names}.");
                }
            }
            catch (PromptTemplateException ex)
            {
                errors.Add(ex.Message);
            }
        }

        return errors;
    }

    /// <summary>Every <c>*.md</c> file the prompt directory currently holds, in a stable order.</summary>
    /// <remarks>
    /// Validation is driven by what is on disk rather than by the catalog's key set, so the two
    /// directions stay asymmetric: a file with no catalog entry is reported as unrecognized, while
    /// a catalog entry with no file is left to <see cref="ReadTemplate"/>'s "not found" error at
    /// the point of use. A directory that legitimately supplies only some templates - a test
    /// fixture, or a partially deployed output folder - therefore raises no spurious startup
    /// errors for the templates it never claimed to provide.
    /// </remarks>
    private List<string> ShippedTemplateFiles()
    {
        if (!Directory.Exists(_promptDirectory))
        {
            return [];
        }

        // AllDirectories mirrors the Prompt\**\*.md content glob, so a template dropped into a
        // subdirectory is seen (and, having no catalog entry, reported) rather than missed.
        return Directory
            .EnumerateFiles(_promptDirectory, "*.md", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(_promptDirectory, path))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string ReadTemplate(string fileName)
    {
        var path = Path.Combine(_promptDirectory, fileName);

        if (!File.Exists(path))
        {
            throw new PromptTemplateException($"Die Prompt-Datei '{fileName}' wurde nicht gefunden: {path}");
        }

        string text;
        try
        {
            // detectEncodingFromByteOrderMarks strips a UTF-8 BOM instead of leaking U+FEFF into the prompt.
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        catch (IOException ex)
        {
            throw new PromptTemplateException($"Die Prompt-Datei '{fileName}' konnte nicht gelesen werden.", ex);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new PromptTemplateException($"Die Prompt-Datei '{fileName}' ist leer.");
        }

        return text;
    }
}
