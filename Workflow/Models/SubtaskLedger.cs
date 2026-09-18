using System.IO;
using System.Text.Json;

namespace Workflow.Models;

/// <summary>
/// The only place that interprets the tracking files of a task. It is static, filesystem-only and
/// uncached: every call reads the current contents from disk, so orchestration, presentation and
/// recovery can share it without agreeing on a refresh protocol.
/// </summary>
/// <remarks>
/// This part of the ledger covers the task-level ordered index. The index is the sole source of
/// execution order: there is no alphabetical fallback and no directory listing anywhere in this
/// code path (requirement 6.1).
/// </remarks>
public static class SubtaskLedger
{
    /// <summary>Highest index schema version this build understands. The field is optional on disk.</summary>
    private const int SupportedIndexVersion = 1;

    /// <summary>Reason carried by an entry whose title is blank, whitespace-only or not a JSON string.</summary>
    private const string BlankTitleReason = "Der Index enthält einen leeren Subtask-Namen.";

    // Same permissiveness as the journal's read path: trailing commas and comments are the two
    // things a hand-edited tracking file most often acquires, and neither changes the meaning.
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Reads the task-level ordered index and normalises it into one entry per unique title.
    /// </summary>
    /// <param name="paths">The tracking paths of the task whose index is read.</param>
    /// <returns>
    /// The entries in declared order, or <see langword="null"/> when the index is <em>unusable</em>.
    /// A usable result is never empty.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// Two failure modes are deliberately distinct. An <em>unusable</em> index - absent, unreadable,
    /// not JSON, not an object, carrying a <c>version</c> above
    /// <see cref="SupportedIndexVersion"/>, or lacking a non-empty <c>subtasks</c> array - yields
    /// null, because no order can be established at all; the caller turns that into the German
    /// configuration error of requirement 2.9. An index that merely <em>contains bad entries</em>
    /// yields entries: a blank or unsafe title becomes a <see cref="SubtaskStatus.Failed"/> state
    /// carrying a reason, and every other entry stays usable. No individual bad entry invalidates
    /// the index (requirement 2.10, design issue 4).
    /// </para>
    /// <para>
    /// Normalisation order is fixed by requirement 2.12: entries are trimmed <em>first</em>, then
    /// de-duplicated with <see cref="StringComparer.OrdinalIgnoreCase"/> keeping the first
    /// occurrence. The count of returned entries is therefore the number of unique titles, which is
    /// the <c>M</c> of the <c>{N} von {M}</c> display and the size of the loop's attempted set. A
    /// hand-count of a malformed index file may disagree with it; the ledger, the loop and the
    /// indicator never do.
    /// </para>
    /// <para>
    /// Every returned entry is either <see cref="SubtaskStatus.Pending"/> (the title is safe and
    /// nothing has been read about it yet) or <see cref="SubtaskStatus.Failed"/> (the title itself
    /// is unusable). Establishing the real outcome from status payloads, descriptions and completion
    /// flags is a separate step; this one never touches a subtask folder.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<SubtaskState>? TryReadIndex(SubtaskPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        string content;
        try
        {
            if (!File.Exists(paths.ResultAbsolute))
            {
                return null;
            }

            content = File.ReadAllText(paths.ResultAbsolute);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(content, DocumentOptions);
            return Normalise(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Applies the schema rules and the trim / de-duplicate / classify normalisation.</summary>
    /// <param name="root">The parsed index document's root element.</param>
    /// <returns>The normalised entries, or null when the document does not satisfy the schema.</returns>
    private static List<SubtaskState>? Normalise(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        JsonElement version = default;
        JsonElement subtasks = default;
        foreach (var property in root.EnumerateObject())
        {
            // Property names are matched case-insensitively, exactly as the journal's read path
            // does; the informational "task" property is read by nobody and simply falls through.
            if (string.Equals(property.Name, "version", StringComparison.OrdinalIgnoreCase))
            {
                version = property.Value;
            }
            else if (string.Equals(property.Name, "subtasks", StringComparison.OrdinalIgnoreCase))
            {
                subtasks = property.Value;
            }
        }

        if (!IsSupportedVersion(version))
        {
            return null;
        }

        // Required and non-empty. An absent, null, empty or non-array value leaves no order to
        // follow, and requirement 6.1 forbids inventing one from the subtasks directory.
        if (subtasks.ValueKind != JsonValueKind.Array || subtasks.GetArrayLength() == 0)
        {
            return null;
        }

        var entries = new List<SubtaskState>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in subtasks.EnumerateArray())
        {
            // A JSON null or a non-string element is an entry-level defect, not a schema defect:
            // it costs its own entry and nothing else, like a blank title.
            var title = element.ValueKind == JsonValueKind.String
                ? (element.GetString() ?? string.Empty).Trim()
                : string.Empty;

            if (!seen.Add(title))
            {
                continue;
            }

            entries.Add(Classify(title));
        }

        return entries;
    }

    /// <summary>Reports whether an optional <c>version</c> value is one this build accepts.</summary>
    /// <param name="version">The value found for the property, or an undefined element when absent.</param>
    /// <returns>True when the version is absent, JSON null, or a number of at most <see cref="SupportedIndexVersion"/>.</returns>
    private static bool IsSupportedVersion(JsonElement version)
    {
        if (version.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return true;
        }

        return version.ValueKind == JsonValueKind.Number
            && version.TryGetInt32(out var value)
            && value <= SupportedIndexVersion;
    }

    /// <summary>Turns one normalised title into its index entry.</summary>
    /// <param name="title">The already trimmed title.</param>
    /// <returns>A pending entry for a safe title, or a failed entry carrying the German reason.</returns>
    private static SubtaskState Classify(string title)
    {
        if (SubtaskPaths.IsValidTitle(title))
        {
            return new SubtaskState(title, SubtaskStatus.Pending);
        }

        // The reason reaches the indicator tooltip (requirement 3.7), so it is German and names the
        // offending title - except when there is no title to name.
        var reason = title.Length == 0
            ? BlankTitleReason
            : $"Unsicherer Subtask-Name: '{title}'.";

        return new SubtaskState(title, SubtaskStatus.Failed, reason);
    }
}
