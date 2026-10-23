using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Workflow.Models;

/// <summary>
/// Reads the gate items a finished task left open: the <c>## Offene Punkte</c> section of the done
/// file (implementation_prompt.md) and the <c>openGateItems</c> array of every completed subtask's
/// <c>status.json</c> (run_subtask.md). Static, filesystem-only and uncached, like
/// <see cref="SubtaskLedger"/>.
/// </summary>
/// <remarks>
/// Nothing here decides completion; the done file and the <c>status</c> field keep doing that. An
/// unreadable or malformed source becomes a read-error item rather than an empty result, because
/// unknown evidence must not look like "nothing open".
/// </remarks>
public static partial class OpenGateItems
{
    /// <summary>Source label of the items taken from the task's done file.</summary>
    public const string ImplementationSource = "Implementierung";

    private const string OpenGateItemsProperty = "openGateItems";

    private const string StatusProperty = "status";

    // The same value, compared the same way, as SubtaskLedger's completion status.
    private const string CompleteValue = "complete";

    private const string UnreadableDoneFile = "Die done-Datei ist nicht lesbar. Beim nächsten Öffnen wird sie neu gelesen.";

    private const string MissingSection = "Die done-Datei enthält keinen Abschnitt „## Offene Punkte“; offene Punkte sind nicht auswertbar.";

    private const string UnreadableStatus = "Die Statusdatei ist nicht lesbar. Beim nächsten Öffnen wird sie neu gelesen.";

    private const string MalformedStatus = "Die Statusdatei ist fehlerhaft; offene Punkte sind nicht auswertbar.";

    private const string MalformedItems = "„openGateItems“ ist keine Liste von Texten; offene Punkte sind nicht auswertbar.";

    // Same permissiveness as SubtaskLedger: hand-edited tracking files gain trailing commas and comments.
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    [GeneratedRegex(@"^#{1,6}\s*Offene\s+Punkte\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SectionHeadingRegex();

    /// <summary>Reads the <c>## Offene Punkte</c> section of a done file.</summary>
    /// <param name="doneAbsolute">Absolute path of the task's done file.</param>
    /// <returns>
    /// No items when the file is absent or blank (the task is not done) or the section says
    /// <c>- keine</c>; one item per bullet otherwise; a read error when the file cannot be read or a
    /// non-blank file has no such section.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="doneAbsolute"/> is null, empty or whitespace.</exception>
    public static OpenGateReport FromDoneFile(string doneAbsolute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(doneAbsolute);

        string content;
        try
        {
            if (!File.Exists(doneAbsolute))
            {
                return OpenGateReport.Empty;
            }

            content = File.ReadAllText(doneAbsolute);
        }
        catch (IOException)
        {
            return ReadError(ImplementationSource, UnreadableDoneFile);
        }
        catch (UnauthorizedAccessException)
        {
            return ReadError(ImplementationSource, UnreadableDoneFile);
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return OpenGateReport.Empty;
        }

        var bullets = SectionBullets(content);

        return bullets is null
            ? ReadError(ImplementationSource, MissingSection)
            : new OpenGateReport(bullets.Select(b => new OpenGateItem(ImplementationSource, b, false)).ToList());
    }

    /// <summary>Reads the <c>openGateItems</c> of every completed subtask, in index order.</summary>
    /// <param name="paths">The tracking paths of the task.</param>
    /// <returns>
    /// No items when the index is unreadable. A subtask whose payload is absent, not
    /// <c>complete</c> or has no <c>openGateItems</c> contributes nothing; one whose payload cannot
    /// be read or parsed, or carries anything but an array of strings, contributes a read error.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is null.</exception>
    /// <remarks>
    /// The ledger supplies titles and order only. Whether a payload is complete is decided here from
    /// the payload itself: the ledger turns a locked or malformed payload into a failure, and its
    /// items would otherwise disappear without a trace while it is locked.
    /// </remarks>
    public static OpenGateReport FromSubtasks(SubtaskPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var snapshot = SubtaskLedger.TryRead(paths);
        if (snapshot is null)
        {
            return OpenGateReport.Empty;
        }

        var items = new List<OpenGateItem>();

        // An entry the index itself rejected carries a title that must never compose a path.
        foreach (var state in snapshot.States.Where(s => SubtaskPaths.IsValidTitle(s.Title)))
        {
            items.AddRange(FromStatusFile(state.Title, paths.SubtaskStatusFile(state.Title)));
        }

        return new OpenGateReport(items);
    }

    /// <summary>Concatenates reports, keeping their order.</summary>
    /// <param name="reports">The reports to join.</param>
    /// <returns>One report holding every item.</returns>
    public static OpenGateReport Combine(params OpenGateReport[] reports) =>
        new(reports.SelectMany(r => r.Items).ToList());

    /// <summary>The copy text of a report, grouped by source in order of first appearance.</summary>
    /// <param name="taskName">The task the report belongs to.</param>
    /// <param name="report">The report to format.</param>
    /// <returns>An empty string for an empty report, otherwise a heading followed by one block per source.</returns>
    public static string Format(string taskName, OpenGateReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (report.IsEmpty)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        text.Append("Offene Punkte – ").Append(taskName).AppendLine();

        foreach (var group in report.Items.GroupBy(i => i.Source, StringComparer.Ordinal))
        {
            text.AppendLine();
            text.AppendLine(group.Key);

            foreach (var item in group)
            {
                text.Append("- ");
                if (item.IsReadError)
                {
                    text.Append("[nicht lesbar] ");
                }

                text.AppendLine(item.Text);
            }
        }

        return text.ToString().TrimEnd();
    }

    // Null when the section is missing. Bullets run until the next heading; a non-bullet line
    // continues the bullet before it, so a wrapped item stays one item.
    private static List<string>? SectionBullets(string content)
    {
        var lines = content.Split('\n').Select(l => l.Trim()).ToList();
        var start = lines.FindIndex(l => SectionHeadingRegex().IsMatch(l));
        if (start < 0)
        {
            return null;
        }

        var bullets = new List<string>();
        foreach (var line in lines.Skip(start + 1))
        {
            if (line.StartsWith('#'))
            {
                break;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                bullets.Add(line[2..].Trim());
            }
            else if (line.Length > 0 && bullets.Count > 0)
            {
                bullets[^1] = bullets[^1] + " " + line;
            }
        }

        return bullets.Where(b => b.Length > 0 && !IsNothingOpen(b)).ToList();
    }

    private static bool IsNothingOpen(string bullet)
    {
        var word = bullet.TrimEnd('.');

        return string.Equals(word, "keine", StringComparison.OrdinalIgnoreCase)
            || string.Equals(word, "none", StringComparison.OrdinalIgnoreCase);
    }

    private static List<OpenGateItem> FromStatusFile(string title, string path)
    {
        string content;
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            content = File.ReadAllText(path);
        }
        catch (IOException)
        {
            return [new OpenGateItem(title, UnreadableStatus, true)];
        }
        catch (UnauthorizedAccessException)
        {
            return [new OpenGateItem(title, UnreadableStatus, true)];
        }

        try
        {
            using var document = JsonDocument.Parse(content, DocumentOptions);
            return Interpret(title, document.RootElement);
        }
        catch (JsonException)
        {
            return [new OpenGateItem(title, MalformedStatus, true)];
        }
    }

    // Materialised before the document is disposed: the strings must not outlive their JsonElement lazily.
    private static List<OpenGateItem> Interpret(string title, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return [new OpenGateItem(title, MalformedStatus, true)];
        }

        // Open items belong to a finished subtask only; a failed or pending one shows in the subtask indicator.
        var status = root.EnumerateObject()
            .FirstOrDefault(p => string.Equals(p.Name, StatusProperty, StringComparison.OrdinalIgnoreCase));
        if (status.Value.ValueKind != JsonValueKind.String
            || !string.Equals(status.Value.GetString(), CompleteValue, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var property = root.EnumerateObject()
            .FirstOrDefault(p => string.Equals(p.Name, OpenGateItemsProperty, StringComparison.OrdinalIgnoreCase));

        // A complete payload without the field predates it; the prompt makes it mandatory from now on.
        if (property.Value.ValueKind == JsonValueKind.Undefined)
        {
            return [];
        }

        if (property.Value.ValueKind != JsonValueKind.Array
            || property.Value.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
        {
            return [new OpenGateItem(title, MalformedItems, true)];
        }

        return property.Value.EnumerateArray()
            .Select(e => e.GetString()!.Trim()) // String kind checked above, so GetString cannot return null.
            .Where(t => t.Length > 0)
            .Select(t => new OpenGateItem(title, t, false))
            .ToList();
    }

    private static OpenGateReport ReadError(string source, string text) =>
        new([new OpenGateItem(source, text, true)]);
}
