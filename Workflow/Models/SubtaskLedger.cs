using System.IO;
using System.Text.Json;

namespace Workflow.Models;

/// <summary>
/// The only place that interprets the tracking files of a task. It is static, filesystem-only and
/// uncached: every call reads the current contents from disk, so orchestration, presentation and
/// recovery can share it without agreeing on a refresh protocol.
/// </summary>
/// <remarks>
/// <para>
/// The index is the sole source of execution order and of the set of entries: there is no
/// alphabetical fallback and no directory listing anywhere in this file (requirement 6.1). Every
/// path it touches is composed by <see cref="SubtaskPaths"/> from a title the index listed.
/// </para>
/// <para>
/// Nothing here is cached and nothing here is persisted. Each call re-reads the current contents,
/// which is what lets the loop, the indicator and recovery derive progress from the tracking files
/// alone, without a stored current-subtask cursor (requirement 4.5).
/// </para>
/// </remarks>
public static class SubtaskLedger
{
    /// <summary>Highest index schema version this build understands. The field is optional on disk.</summary>
    private const int SupportedIndexVersion = 1;

    /// <summary>Reason carried by an entry whose title is blank, whitespace-only or not a JSON string.</summary>
    private const string BlankTitleReason = "Der Index enthält einen leeren Subtask-Namen.";

    /// <summary>The only status value that completes a subtask. Compared case-insensitively.</summary>
    private const string CompleteValue = "complete";

    /// <summary>The only status value that fails a subtask on its own. Compared case-insensitively.</summary>
    private const string FailedValue = "failed";

    /// <summary>Reason used when a subtask reports <c>failed</c> without a <c>failreason</c>.</summary>
    private const string UnexplainedFailureReason = "Der Subtask meldet einen Fehlschlag ohne Begründung.";

    /// <summary>Reason used when a subtask's status payload is unreadable or malformed.</summary>
    private const string UnusableStatusReason = "Die Statusdatei des Subtasks ist unlesbar oder fehlerhaft.";

    // Same permissiveness as the journal's read path: trailing commas and comments are the two
    // things a hand-edited tracking file most often acquires, and neither changes the meaning.
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Reads everything the tracking files say about one task's subtasks: the states in index order
    /// plus the counts derived from them.
    /// </summary>
    /// <param name="paths">The tracking paths of the task whose evidence is read.</param>
    /// <returns>
    /// The snapshot, or <see langword="null"/> when the ordered index itself is unusable. A null
    /// result is not "nothing is done"; it is "the evidence could not be read", and requirement 4.6
    /// forbids completing or demoting a task from it.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// Each entry is derived from the resolved table in the design's <em>Tracking Files</em> section,
    /// evaluated in this order:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// an unsafe or blank title is already <see cref="SubtaskStatus.Failed"/> when the index is
    /// parsed and is never taken to the filesystem (requirement 2.10);
    /// </description></item>
    /// <item><description>
    /// a status payload that reads <em>and</em> parses decides the outcome on <c>complete</c> and
    /// <c>failed</c> alone, compared case-insensitively;
    /// </description></item>
    /// <item><description>
    /// a payload that cannot be read or does not parse is <see cref="SubtaskStatus.Failed"/>.
    /// Settling the race against a session that is still publishing is the caller's job, at 200 ms
    /// up to five times (requirement 2.11, design issue 10); the ledger reports what is on disk now;
    /// </description></item>
    /// <item><description>
    /// every other parsed value - including <c>pending</c> and anything unrecognised - and an absent
    /// payload leave the entry unfinished (design issue 1), and an unfinished entry then needs a
    /// usable description: a missing, empty or unreadable <c>subtask.md</c> fails that entry and only
    /// that entry (requirement 2.10, design issue 5).
    /// </description></item>
    /// </list>
    /// <para>
    /// A completion status therefore outranks both a description deleted after the fact and a flag
    /// that was never published, which is exactly what requirement 4.4 asks of recovery. The
    /// per-subtask completion flag is not evidence here at all: both table rows that mention it are
    /// Pending, so the flag remains what it is for the orchestrator - the trigger to start settling,
    /// never an outcome by itself.
    /// </para>
    /// </remarks>
    public static SubtaskSnapshot? TryRead(SubtaskPaths paths)
    {
        var index = TryReadIndex(paths);
        if (index is null)
        {
            return null;
        }

        var states = new List<SubtaskState>(index.Count);
        foreach (var entry in index)
        {
            // An entry the index itself failed carries a title that must never compose a path, so it
            // is passed through untouched - reason and all.
            states.Add(entry.Status == SubtaskStatus.Failed ? entry : Derive(paths, entry.Title));
        }

        return new SubtaskSnapshot(states);
    }

    /// <summary>Reports whether an existing decomposition may be reused instead of running a new one.</summary>
    /// <param name="paths">The tracking paths of the task.</param>
    /// <returns>True when the ordered index is readable.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is null.</exception>
    /// <remarks>
    /// The readable ordered index is the <em>only</em> condition (requirement 2.3, design issue 5).
    /// Descriptions are deliberately not required, not even one per entry: an agent that tidies up a
    /// finished subtask's <c>subtask.md</c> would otherwise destroy a curated index and force a whole
    /// rebuild. A folder without a usable description fails its own entry in <see cref="TryRead"/>
    /// instead, and the run continues with the others.
    /// </remarks>
    public static bool IsDecomposed(SubtaskPaths paths) => TryReadIndex(paths) is not null;

    /// <summary>Reports whether every subtask of the task is complete.</summary>
    /// <param name="paths">The tracking paths of the task.</param>
    /// <returns>True when the index is readable and every one of its entries is complete.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is null.</exception>
    /// <remarks>
    /// The condition is <c>Completed == Total</c> on a freshly read snapshot (design issue 6a), never
    /// "no failures": an all-pending task has zero failures and is plainly not done. An unusable
    /// index answers false rather than true, so unreadable final evidence can never complete a task
    /// (requirement 4.6, design issue 9). <c>Total &gt; 0</c> is stated rather than assumed; the index
    /// reader never yields an empty usable result, and completion must not hinge on that staying true.
    /// </remarks>
    public static bool AllComplete(SubtaskPaths paths)
    {
        var snapshot = TryRead(paths);

        return snapshot is not null && snapshot.Total > 0 && snapshot.Completed == snapshot.Total;
    }

    /// <summary>
    /// Reads the task-level ordered index and normalises it into one entry per unique title. This is
    /// a <em>deliberate fourth member</em> beyond the three the design's <em>Paths and Ledger</em>
    /// section names, retained rather than hidden because the index step answers a question the other
    /// three cannot: "is there an order at all", separately from "what does the evidence say".
    /// <see cref="IsDecomposed"/> reduces that to a boolean and <see cref="TryRead"/> folds it into a
    /// snapshot, so a caller that needs the raw ordered titles - and a test that needs to pin
    /// requirements 2.12 and 6.1 without staging a subtask folder per entry - would otherwise have to
    /// re-parse <c>result.json</c> itself, which is exactly the duplication this class exists to
    /// prevent. It reads the index only and never touches a subtask folder.
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

    /// <summary>Derives one safely titled entry's state from its status payload and description.</summary>
    /// <param name="paths">The tracking paths of the task.</param>
    /// <param name="title">A title that already satisfies <see cref="SubtaskPaths.IsValidTitle"/>.</param>
    /// <returns>The state the evidence on disk establishes for that entry.</returns>
    private static SubtaskState Derive(SubtaskPaths paths, string title)
    {
        var (evidence, reason) = ReadStatus(paths.SubtaskStatusFile(title));

        switch (evidence)
        {
            case StatusEvidence.Complete:
                // The precedence rule lives here: nothing below this line can undo a completion, so a
                // description deleted afterwards or a flag never published cannot demote it (4.4).
                return new SubtaskState(title, SubtaskStatus.Complete);

            case StatusEvidence.Failed:
                return new SubtaskState(
                    title,
                    SubtaskStatus.Failed,
                    string.IsNullOrWhiteSpace(reason) ? UnexplainedFailureReason : reason);

            case StatusEvidence.Unusable:
                return new SubtaskState(title, SubtaskStatus.Failed, UnusableStatusReason);

            default:
                // Absent or undecided: the entry is unfinished, and an unfinished entry is only
                // attemptable if it still has something to attempt.
                return HasDescription(paths, title)
                    ? new SubtaskState(title, SubtaskStatus.Pending)
                    : new SubtaskState(title, SubtaskStatus.Failed, $"Keine Beschreibung für '{title}'.");
        }
    }

    /// <summary>Reads one subtask's status payload.</summary>
    /// <param name="path">Absolute path of that subtask's <c>status.json</c>.</param>
    /// <returns>What the payload establishes, and the <c>failreason</c> it carried, if any.</returns>
    private static (StatusEvidence Evidence, string? Reason) ReadStatus(string path)
    {
        string content;
        try
        {
            if (!File.Exists(path))
            {
                return (StatusEvidence.Absent, null);
            }

            content = File.ReadAllText(path);
        }
        catch (IOException)
        {
            // Being locked by the publishing session is the common case; the caller settles it.
            return (StatusEvidence.Unusable, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (StatusEvidence.Unusable, null);
        }

        try
        {
            using var document = JsonDocument.Parse(content, DocumentOptions);
            return Interpret(document.RootElement);
        }
        catch (JsonException)
        {
            return (StatusEvidence.Unusable, null);
        }
    }

    /// <summary>Applies the closed status vocabulary to a parsed payload.</summary>
    /// <param name="root">The parsed payload's root element.</param>
    /// <returns>What the payload establishes, and the <c>failreason</c> it carried, if any.</returns>
    /// <remarks>
    /// Only <c>status</c> and the optional <c>failreason</c> are read, case-insensitively, and every
    /// other property is ignored. A root that is not an object is not a status payload and counts as
    /// malformed. A <c>status</c> that is absent, JSON null or not a string simply is not one of the
    /// two decisive values, which by design issue 1 means "not done yet" rather than "broken".
    /// </remarks>
    private static (StatusEvidence Evidence, string? Reason) Interpret(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return (StatusEvidence.Unusable, null);
        }

        string? status = null;
        string? reason = null;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (string.Equals(property.Name, "status", StringComparison.OrdinalIgnoreCase))
            {
                status = property.Value.GetString();
            }
            else if (string.Equals(property.Name, "failreason", StringComparison.OrdinalIgnoreCase))
            {
                reason = property.Value.GetString();
            }
        }

        if (string.Equals(status, CompleteValue, StringComparison.OrdinalIgnoreCase))
        {
            return (StatusEvidence.Complete, null);
        }

        return string.Equals(status, FailedValue, StringComparison.OrdinalIgnoreCase)
            ? (StatusEvidence.Failed, reason)
            : (StatusEvidence.Undecided, null);
    }

    /// <summary>Reports whether one subtask still has a usable description to attempt.</summary>
    /// <param name="paths">The tracking paths of the task.</param>
    /// <param name="title">A title that already satisfies <see cref="SubtaskPaths.IsValidTitle"/>.</param>
    /// <returns>True when <c>subtask.md</c> exists and carries more than whitespace.</returns>
    /// <remarks>
    /// A whitespace-only file counts as empty: requirement 2.10 asks for a <em>usable</em>
    /// description, and a blank one would be rendered into a subtask prompt that says nothing. An
    /// unreadable file is treated the same way - it fails that entry, not the index.
    /// </remarks>
    private static bool HasDescription(SubtaskPaths paths, string title)
    {
        var path = paths.SubtaskMarkdown(title);

        try
        {
            return File.Exists(path) && !string.IsNullOrWhiteSpace(File.ReadAllText(path));
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>What one subtask's status payload establishes, before the description is consulted.</summary>
    private enum StatusEvidence
    {
        /// <summary>No <c>status.json</c> exists yet. Unfinished, never a failure.</summary>
        Absent,

        /// <summary>The payload could not be read or did not parse as a status object.</summary>
        Unusable,

        /// <summary>The payload parsed but carries neither decisive value. Unfinished.</summary>
        Undecided,

        /// <summary>The payload carries <c>complete</c>.</summary>
        Complete,

        /// <summary>The payload carries <c>failed</c>.</summary>
        Failed,
    }
}
