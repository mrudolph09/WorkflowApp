using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Workflow.Models;

namespace Workflow.Services;

/// <summary>The complete set of tokens a prompt template may use.</summary>
public static class PromptVariables
{
    /// <summary>Token names, without braces.</summary>
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

    /// <summary>Builds the substitution dictionary for one task.</summary>
    /// <param name="paths">The task's path set.</param>
    /// <param name="taskDescription">The plain-text task description.</param>
    /// <returns>A dictionary covering every known token.</returns>
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

        foreach (var definition in PhaseCatalog.All)
        {
            try
            {
                var text = ReadTemplate(definition.PromptFile);

                var unknown = TokenRegex()
                    .Matches(text)
                    .Select(m => m.Groups["name"].Value)
                    .Where(n => !PromptVariables.KnownNames.Contains(n))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (unknown.Count > 0)
                {
                    var names = string.Join(", ", unknown.Select(n => "{" + n + "}"));
                    errors.Add($"Die Prompt-Datei '{definition.PromptFile}' enthält unbekannte Platzhalter: {names}.");
                }
            }
            catch (PromptTemplateException ex)
            {
                errors.Add(ex.Message);
            }
        }

        return errors;
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
