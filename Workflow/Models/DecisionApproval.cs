using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Workflow.Models;

/// <summary>The hashes of a task's decisions and index at one moment - what a hold showed the user.</summary>
/// <param name="DecisionsSha256">Upper-case hex SHA-256 of <c>decisions.md</c>.</param>
/// <param name="IndexSha256">Upper-case hex SHA-256 of the task-level <c>result.json</c> (the index).</param>
public sealed record DecisionSnapshot(string DecisionsSha256, string IndexSha256)
{
    /// <summary>The first eight hex digits of <see cref="DecisionsSha256"/>, as the hold names them.</summary>
    public string ShortDecisionsHash => DecisionsSha256[..8];
}

/// <summary>
/// The user's approval of a task's <c>decisions.md</c>, bound by hash to exactly the decisions and the
/// index it was given for (B7).
/// </summary>
/// <remarks>
/// <para>
/// The approval lives in application-owned storage, never in the task folder an agent can write:
/// <c>&lt;AppStorage.Root&gt;\approvals\&lt;sha256 of the task path&gt;.json</c>, holding
/// <c>decisionsSha256</c>, <c>indexSha256</c> (the task-level <c>result.json</c>), <c>taskTitle</c>,
/// <c>taskPath</c> and <c>approvedUtc</c>. It is valid only while both hashes match the files on disk
/// and title and path are this task's, so an edited or regenerated <c>decisions.md</c> or a new
/// index reads as "not approved" without anything having to delete it. A
/// <c>decisions.approved.json</c> in the task folder - where earlier versions kept it - is ignored.
/// </para>
/// <para>
/// Only the application writes it, and only for the hashes a hold showed the user
/// (<see cref="TryApprove"/>); the pre-flight prompt tells its agent not to write approvals at all.
/// </para>
/// </remarks>
public static class DecisionApproval
{
    /// <summary>The pre-decided answers of a task, in its tracking task folder.</summary>
    public const string DecisionsFileName = "decisions.md";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>Absolute path of the task's <c>decisions.md</c>.</summary>
    /// <param name="tracking">The task's tracking paths.</param>
    /// <returns>The path; the file may not exist.</returns>
    public static string DecisionsFile(SubtaskPaths tracking)
    {
        ArgumentNullException.ThrowIfNull(tracking);

        return Path.Combine(tracking.TaskDirectory, DecisionsFileName);
    }

    /// <summary>Absolute path of the task's approval in application-owned storage.</summary>
    /// <param name="tracking">The task's tracking paths.</param>
    /// <returns>The path; the file may not exist.</returns>
    public static string ApprovalFile(SubtaskPaths tracking)
    {
        ArgumentNullException.ThrowIfNull(tracking);

        return Path.Combine(AppStorage.Root, "approvals", AppStorage.KeyFor(tracking.TaskDirectory) + ".json");
    }

    /// <summary>Reports whether the task has a non-empty <c>decisions.md</c>.</summary>
    /// <param name="tracking">The task's tracking paths.</param>
    /// <returns>True when the file exists and holds at least one byte.</returns>
    public static bool HasDecisions(SubtaskPaths tracking)
    {
        var file = new FileInfo(DecisionsFile(tracking));

        return file.Exists && file.Length > 0;
    }

    /// <summary>Hashes the decisions and the index as they are on disk now.</summary>
    /// <param name="tracking">The task's tracking paths.</param>
    /// <returns>The snapshot, or null when either file is absent, empty or unreadable.</returns>
    public static DecisionSnapshot? Capture(SubtaskPaths tracking)
    {
        var decisions = Sha256(DecisionsFile(tracking));
        var index = Sha256(tracking.ResultAbsolute);

        return decisions is null || index is null ? null : new DecisionSnapshot(decisions, index);
    }

    /// <summary>Reports whether the stored approval covers the decisions and the index on disk.</summary>
    /// <param name="tracking">The task's tracking paths.</param>
    /// <param name="taskTitle">The current task's title.</param>
    /// <returns>
    /// True only when the approval parses, both of its hashes equal the current files', its title is
    /// <paramref name="taskTitle"/> and its path is this task's; anything unreadable is "not approved".
    /// </returns>
    public static bool IsValid(SubtaskPaths tracking, string taskTitle)
    {
        var current = Capture(tracking);
        if (current is null)
        {
            return false;
        }

        try
        {
            using var approval = JsonDocument.Parse(File.ReadAllText(ApprovalFile(tracking)));
            var root = approval.RootElement;

            return root.ValueKind == JsonValueKind.Object
                && HashEquals(root, "decisionsSha256", current.DecisionsSha256)
                && HashEquals(root, "indexSha256", current.IndexSha256)
                && TextEquals(root, "taskTitle", taskTitle, StringComparison.Ordinal)
                && TextEquals(root, "taskPath", AppStorage.NormalizePath(tracking.TaskDirectory), StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Records the user's approval of exactly the hashes a hold showed - and only while the files on
    /// disk still have them - atomically (a temporary file, then a rename).
    /// </summary>
    /// <param name="tracking">The task's tracking paths.</param>
    /// <param name="taskTitle">The current task's title.</param>
    /// <param name="shown">The snapshot the hold showed the user.</param>
    /// <param name="approvedUtc">When the user approved.</param>
    /// <returns>
    /// False, writing nothing, when the files changed since <paramref name="shown"/> was taken or can
    /// no longer be hashed.
    /// </returns>
    public static bool TryApprove(SubtaskPaths tracking, string taskTitle, DecisionSnapshot shown, DateTimeOffset approvedUtc)
    {
        ArgumentNullException.ThrowIfNull(shown);

        if (Capture(tracking) != shown)
        {
            return false;
        }

        var path = ApprovalFile(tracking);
        var temporary = path + ".tmp";

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(
                new ApprovalRecord(
                    shown.DecisionsSha256,
                    shown.IndexSha256,
                    taskTitle,
                    AppStorage.NormalizePath(tracking.TaskDirectory),
                    approvedUtc),
                WriteOptions));
        File.Move(temporary, path, overwrite: true);

        return true;
    }

    private static bool HashEquals(JsonElement root, string name, string expected) =>
        TextEquals(root, name, expected, StringComparison.OrdinalIgnoreCase);

    private static bool TextEquals(JsonElement root, string name, string expected, StringComparison comparison) =>
        root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && string.Equals(value.GetString(), expected, comparison);

    /// <summary>The upper-case hex SHA-256 of a non-empty file, or null when it is absent, empty or unreadable.</summary>
    private static string? Sha256(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);

            return bytes.Length == 0 ? null : Convert.ToHexString(SHA256.HashData(bytes));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record ApprovalRecord(
        string DecisionsSha256,
        string IndexSha256,
        string TaskTitle,
        string TaskPath,
        DateTimeOffset ApprovedUtc);
}
