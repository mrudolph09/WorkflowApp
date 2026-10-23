using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Workflow.Models;

/// <summary>One step of a desktop job, as <c>desktopjob.json</c> declares it.</summary>
/// <param name="Id">Unique within the request; matches <c>^[a-z0-9_-]{1,32}$</c>.</param>
/// <param name="Script">A plain file name inside the subtask folder.</param>
/// <param name="TimeoutMinutes">1-240.</param>
/// <param name="Marker">Absolute path of the file the script creates when it succeeded.</param>
public sealed record DesktopJobStep(string Id, string Script, int TimeoutMinutes, string Marker);

/// <summary>A validated <c>desktopjob.json</c> (version 2).</summary>
/// <param name="RequestId">Identifies this request; a result for any other id never counts.</param>
/// <param name="IdleMinutes">How long the user must have been away before a step may start, 1-120.</param>
/// <param name="Steps">1-10 steps, run strictly in this order.</param>
public sealed record DesktopJobRequest(Guid RequestId, int IdleMinutes, IReadOnlyList<DesktopJobStep> Steps);

/// <summary>The outcome of reading <c>desktopjob.json</c>: the request, or why there is none.</summary>
/// <param name="Request">The validated request, or null when the file is missing or invalid.</param>
/// <param name="Error">German reason when <paramref name="Request"/> is null; otherwise null.</param>
/// <param name="RequestId">The request id when one could be read, even from an otherwise invalid file.</param>
public sealed record DesktopJobRequestRead(DesktopJobRequest? Request, string? Error, string? RequestId);

/// <summary>The outcomes a desktop job step can end with, as written to the result file.</summary>
public static class DesktopJobOutcome
{
    /// <summary>The script exited 0 and its marker exists.</summary>
    public const string Completed = "completed";

    /// <summary>The step ran into its <c>timeoutMinutes</c> and was killed.</summary>
    public const string Timeout = "timeout";

    /// <summary>Real (not injected) keyboard or mouse input arrived while the step ran; it was killed.</summary>
    public const string AbortedUserInput = "aborted_user_input";

    /// <summary>The workstation was locked while the step ran; it was killed.</summary>
    public const string AbortedLocked = "aborted_locked";

    /// <summary>The machine was not quiet enough to start; nothing was launched.</summary>
    public const string AbortedBusy = "aborted_busy";

    /// <summary>The request was invalid, the script was missing or the process could not be launched.</summary>
    public const string StartFailed = "start_failed";

    /// <summary>The script exited non-zero, or exited 0 without creating its marker.</summary>
    public const string ScriptFailed = "script_failed";

    /// <summary>Reports whether an outcome uses up one of a step's attempts in a run.</summary>
    /// <param name="outcome">The outcome, or null for a step that was never started.</param>
    /// <returns>
    /// True for the outcomes a retry would most likely repeat. A busy machine and a locked session
    /// say nothing about the job itself, so they never count and are simply retried later.
    /// </returns>
    public static bool CountsAsAttempt(string? outcome) =>
        outcome is Timeout or AbortedUserInput or StartFailed or ScriptFailed;

    /// <summary>Reports whether an outcome shows that the job itself is wrong: the step ran and failed.</summary>
    /// <param name="outcome">The outcome, or null for a step that was never started.</param>
    /// <returns>
    /// True for a timeout and a failed script. Running the same request again would most likely end the
    /// same way, so a session rewrites the request instead of the run repeating it.
    /// </returns>
    public static bool NeedsRepair(string? outcome) => outcome is Timeout or ScriptFailed;
}

/// <summary>What a deferred entry's desktop job means for the entry right now.</summary>
public enum DesktopJobReadiness
{
    /// <summary>The job still has to run: no valid request, no result for it, or the next step has not yet run and failed with its current script.</summary>
    Waiting,

    /// <summary>Every step completed for the current request and scripts; a session verifies the result.</summary>
    Delivered,

    /// <summary>The next step ran with its current script and ended in a repair outcome; a session rewrites the request.</summary>
    NeedsRepair,
}

/// <summary>What one step's latest attempt produced, as <c>desktopjob.result.json</c> records it.</summary>
/// <param name="StepId">The step's id.</param>
/// <param name="ScriptSha256">SHA-256 of the script bytes the attempt ran, or null when there was no script.</param>
/// <param name="Outcome">One of <see cref="DesktopJobOutcome"/>.</param>
/// <param name="ExitCode">The script's exit code, or null when it did not exit on its own.</param>
/// <param name="MarkerFound">Whether the step's marker existed after the attempt.</param>
/// <param name="StartedUtc">When the attempt started.</param>
/// <param name="EndedUtc">When the attempt ended.</param>
/// <param name="Attempts">How many attempts this request has recorded for the step, including this one.</param>
/// <param name="Reason">German explanation for <see cref="DesktopJobOutcome.StartFailed"/>; otherwise null.</param>
public sealed record DesktopJobStepResult(
    string StepId,
    string? ScriptSha256,
    string Outcome,
    int? ExitCode,
    bool MarkerFound,
    DateTimeOffset StartedUtc,
    DateTimeOffset EndedUtc,
    int Attempts,
    string? Reason = null);

/// <summary>The content of <c>desktopjob.result.json</c>, rewritten after every step.</summary>
/// <param name="RequestId">The request the steps belong to, or null when none could be read.</param>
/// <param name="ConfigSha256">SHA-256 of the normalised request, or null for an invalid request.</param>
/// <param name="Steps">One entry per step attempted under this request and config, in step order.</param>
/// <param name="RequestError">Why <c>desktopjob.json</c> could not be used; otherwise null.</param>
public sealed record DesktopJobResult(
    string? RequestId,
    string? ConfigSha256,
    IReadOnlyList<DesktopJobStepResult> Steps,
    string? RequestError = null);

/// <summary>
/// The only place that interprets a subtask folder's desktop job files: the request a session wrote,
/// the result the runner writes, and whether that result answers the current request. Static and
/// uncached like <see cref="SubtaskLedger"/>, which asks <see cref="Readiness"/> for every
/// deferred entry.
/// </summary>
/// <remarks>
/// A result counts only for the request it was made for: the same <c>requestId</c>, the same
/// <c>configSha256</c> (so editing a step's timeout or marker runs the job again) and, per step, the
/// same script bytes. A result copied back from an older run therefore never counts, and neither
/// does one whose script was changed after it ran.
/// </remarks>
public static partial class DesktopJobFiles
{
    /// <summary>The request file a session writes into its subtask folder.</summary>
    public const string RequestFileName = "desktopjob.json";

    /// <summary>The result file the runner writes next to it.</summary>
    public const string ResultFileName = "desktopjob.result.json";

    private const int RequestVersion = 2;
    private const int MaxSteps = 10;
    private const int MaxIdleMinutes = 120;
    private const int MaxTimeoutMinutes = 240;

    // Same permissiveness as the ledger: comments and trailing commas do not change the meaning.
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonSerializerOptions ResultWriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions ResultReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>The log file one step's attempts append to.</summary>
    /// <param name="stepId">A validated step id.</param>
    /// <returns>The file name, relative to the subtask folder.</returns>
    public static string LogFileName(string stepId) => $"desktopjob.{stepId}.log";

    /// <summary>Reads and validates <c>desktopjob.json</c>.</summary>
    /// <param name="subtaskDirectory">The subtask folder.</param>
    /// <returns>The request, or the German reason it cannot be used.</returns>
    public static DesktopJobRequestRead ReadRequest(string subtaskDirectory)
    {
        var path = Path.Combine(subtaskDirectory, RequestFileName);

        string content;
        try
        {
            if (!File.Exists(path))
            {
                return Invalid($"{RequestFileName} fehlt.", null);
            }

            content = File.ReadAllText(path);
        }
        catch (IOException)
        {
            return Invalid($"{RequestFileName} ist nicht lesbar.", null);
        }
        catch (UnauthorizedAccessException)
        {
            return Invalid($"{RequestFileName} ist nicht lesbar.", null);
        }

        try
        {
            using var document = JsonDocument.Parse(content, DocumentOptions);
            return Validate(document.RootElement);
        }
        catch (JsonException)
        {
            return Invalid($"{RequestFileName} ist kein gültiges JSON.", null);
        }
    }

    /// <summary>SHA-256 of the normalised request: re-serialised in canonical property order.</summary>
    /// <param name="request">The validated request.</param>
    /// <returns>Upper-case hex.</returns>
    /// <remarks>
    /// Whitespace, comments, property order and spelling of the file do not change it; every value
    /// that changes what runs does.
    /// </remarks>
    public static string ConfigSha256(DesktopJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", RequestVersion);
            writer.WriteString("requestId", request.RequestId.ToString("D"));
            writer.WriteNumber("idleMinutes", request.IdleMinutes);
            writer.WriteStartArray("steps");
            foreach (var step in request.Steps)
            {
                writer.WriteStartObject();
                writer.WriteString("id", step.Id);
                writer.WriteString("script", step.Script);
                writer.WriteNumber("timeoutMinutes", step.TimeoutMinutes);
                writer.WriteString("marker", step.Marker);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan));
    }

    /// <summary>SHA-256 of a step script's bytes as they are now.</summary>
    /// <param name="subtaskDirectory">The subtask folder.</param>
    /// <param name="script">The step's validated script file name.</param>
    /// <returns>Upper-case hex, or null when the script cannot be read.</returns>
    public static string? ScriptSha256(string subtaskDirectory, string script)
    {
        try
        {
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(subtaskDirectory, script))));
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

    /// <summary>
    /// The authoritative result of a subtask folder's desktop job, in application-owned storage:
    /// <c>&lt;AppStorage.Root&gt;\desktopjobs\&lt;sha256 of the folder path&gt;\result.json</c>.
    /// </summary>
    /// <param name="subtaskDirectory">The subtask folder.</param>
    /// <returns>The path; the file may not exist.</returns>
    /// <remarks>
    /// Only the runner writes it. The <c>desktopjob.result.json</c> in the subtask folder is an
    /// informational copy for the verification session: an agent can write that one, so readiness
    /// and step resumption never read it.
    /// </remarks>
    public static string AuthoritativeResultPath(string subtaskDirectory) =>
        Path.Combine(AppStorage.Root, "desktopjobs", AppStorage.KeyFor(subtaskDirectory), "result.json");

    /// <summary>Reads the authoritative result (<see cref="AuthoritativeResultPath"/>), never the folder copy.</summary>
    /// <param name="subtaskDirectory">The subtask folder.</param>
    /// <returns>The result, or null when it is absent, unreadable or malformed.</returns>
    public static DesktopJobResult? ReadResult(string subtaskDirectory)
    {
        try
        {
            var path = AuthoritativeResultPath(subtaskDirectory);
            if (!File.Exists(path))
            {
                return null;
            }

            var result = JsonSerializer.Deserialize<DesktopJobResult>(File.ReadAllText(path), ResultReadOptions);

            // A hand-edited file may omit the array the constructor declares non-null.
            return result?.Steps is null ? null : result;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the result atomically (a temporary file, then a rename): first the authoritative copy
    /// in application-owned storage, then the informational <c>desktopjob.result.json</c> in the
    /// subtask folder.
    /// </summary>
    /// <param name="subtaskDirectory">The subtask folder.</param>
    /// <param name="result">The complete result.</param>
    public static void WriteResult(string subtaskDirectory, DesktopJobResult result)
    {
        var content = JsonSerializer.Serialize(result, ResultWriteOptions);
        var authoritative = AuthoritativeResultPath(subtaskDirectory);

        Directory.CreateDirectory(Path.GetDirectoryName(authoritative)!);
        WriteAtomically(authoritative, content);
        WriteAtomically(Path.Combine(subtaskDirectory, ResultFileName), content);
    }

    private static void WriteAtomically(string path, string content)
    {
        var temporary = path + ".tmp";

        File.WriteAllText(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Returns <paramref name="result"/> only when it was made for exactly this request.</summary>
    /// <param name="request">The current request.</param>
    /// <param name="result">The result on disk, if any.</param>
    /// <returns>The result when its request id and config hash match; otherwise null.</returns>
    public static DesktopJobResult? MatchingResult(DesktopJobRequest request, DesktopJobResult? result)
    {
        ArgumentNullException.ThrowIfNull(request);

        return result is not null
            && Guid.TryParse(result.RequestId, out var id)
            && id == request.RequestId
            && string.Equals(result.ConfigSha256, ConfigSha256(request), StringComparison.OrdinalIgnoreCase)
                ? result
                : null;
    }

    /// <summary>Finds the first step without a completed result for its current script.</summary>
    /// <param name="subtaskDirectory">The subtask folder.</param>
    /// <param name="request">The current request.</param>
    /// <param name="matching">The result for this request, from <see cref="MatchingResult"/>.</param>
    /// <returns>The step's index, or -1 when every step is complete.</returns>
    public static int NextStepIndex(string subtaskDirectory, DesktopJobRequest request, DesktopJobResult? matching)
    {
        ArgumentNullException.ThrowIfNull(request);

        for (var index = 0; index < request.Steps.Count; index++)
        {
            var step = request.Steps[index];
            var done = matching?.Steps.FirstOrDefault(
                entry => string.Equals(entry.StepId, step.Id, StringComparison.Ordinal));

            if (done is null
                || !string.Equals(done.Outcome, DesktopJobOutcome.Completed, StringComparison.Ordinal)
                || done.ScriptSha256 is null
                || !string.Equals(done.ScriptSha256, ScriptSha256(subtaskDirectory, step.Script), StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// What the folder's desktop job means for its deferred entry: still to run, delivered for the
    /// current request, or failed in a way that only a rewritten request can fix.
    /// </summary>
    /// <param name="subtaskDirectory">The subtask folder.</param>
    /// <returns>
    /// <see cref="DesktopJobReadiness.Delivered"/> when every step completed with its current script;
    /// <see cref="DesktopJobReadiness.NeedsRepair"/> when the next step ran with its current script and
    /// timed out or failed; otherwise <see cref="DesktopJobReadiness.Waiting"/>.
    /// </returns>
    public static DesktopJobReadiness Readiness(string subtaskDirectory)
    {
        var request = ReadRequest(subtaskDirectory).Request;
        if (request is null)
        {
            return DesktopJobReadiness.Waiting;
        }

        var matching = MatchingResult(request, ReadResult(subtaskDirectory));
        if (matching is null)
        {
            return DesktopJobReadiness.Waiting;
        }

        var index = NextStepIndex(subtaskDirectory, request, matching);
        if (index < 0)
        {
            return DesktopJobReadiness.Delivered;
        }

        var step = request.Steps[index];

        return RepairOutcome(matching.Steps, step, ScriptSha256(subtaskDirectory, step.Script)) is not null
            ? DesktopJobReadiness.NeedsRepair
            : DesktopJobReadiness.Waiting;
    }

    /// <summary>The repair outcome a step's recorded attempt ended in, when it ran with exactly this script.</summary>
    /// <param name="recorded">The entries of the result for the current request.</param>
    /// <param name="step">The step about to be judged or run.</param>
    /// <param name="currentScriptSha256">The hash of the step's script now; null when it cannot be read.</param>
    /// <returns>The recorded <c>timeout</c> or <c>script_failed</c>; otherwise null.</returns>
    public static string? RepairOutcome(
        IReadOnlyList<DesktopJobStepResult> recorded,
        DesktopJobStep step,
        string? currentScriptSha256)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(step);

        var entry = recorded.FirstOrDefault(
            candidate => string.Equals(candidate.StepId, step.Id, StringComparison.Ordinal));

        // An edited script is a fresh run, not a repair case: the hashes must match.
        return entry is not null
            && DesktopJobOutcome.NeedsRepair(entry.Outcome)
            && entry.ScriptSha256 is not null
            && currentScriptSha256 is not null
            && string.Equals(entry.ScriptSha256, currentScriptSha256, StringComparison.OrdinalIgnoreCase)
                ? entry.Outcome
                : null;
    }

    /// <summary>
    /// Reports whether the folder's desktop job has delivered for the current request: a result with
    /// the same request id and config hash, and every step completed with its current script.
    /// </summary>
    /// <param name="subtaskDirectory">The subtask folder.</param>
    /// <returns>True when a verification session may look at the result.</returns>
    public static bool IsResultReady(string subtaskDirectory) =>
        Readiness(subtaskDirectory) == DesktopJobReadiness.Delivered;

    [GeneratedRegex("^[a-z0-9_-]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex StepIdPattern();

    private static DesktopJobRequestRead Invalid(string reason, string? requestId) =>
        new(null, reason, requestId);

    /// <summary>Applies the version 2 schema. Property names are matched case-insensitively.</summary>
    private static DesktopJobRequestRead Validate(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Invalid($"{RequestFileName} ist kein JSON-Objekt.", null);
        }

        var version = Property(root, "version");
        var rawId = Property(root, "requestId");
        var idle = Property(root, "idleMinutes");
        var steps = Property(root, "steps");

        var requestText = rawId.ValueKind == JsonValueKind.String ? rawId.GetString() : null;
        var parsedId = Guid.TryParse(requestText, out var requestId) ? requestId.ToString("D") : null;

        if (!(version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var number) && number == RequestVersion))
        {
            return Invalid($"{RequestFileName}: version muss {RequestVersion} sein.", parsedId);
        }

        if (parsedId is null)
        {
            return Invalid($"{RequestFileName}: requestId ist keine GUID.", null);
        }

        if (!TryInt(idle, 1, MaxIdleMinutes, out var idleMinutes))
        {
            return Invalid($"{RequestFileName}: idleMinutes muss zwischen 1 und {MaxIdleMinutes} liegen.", parsedId);
        }

        if (steps.ValueKind != JsonValueKind.Array || steps.GetArrayLength() is 0 or > MaxSteps)
        {
            return Invalid($"{RequestFileName}: steps muss 1 bis {MaxSteps} Schritte enthalten.", parsedId);
        }

        var parsed = new List<DesktopJobStep>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in steps.EnumerateArray())
        {
            var (step, error) = ValidateStep(element);
            if (step is null)
            {
                return Invalid($"{RequestFileName}: {error}", parsedId);
            }

            if (!ids.Add(step.Id))
            {
                return Invalid($"{RequestFileName}: Schritt-ID '{step.Id}' ist doppelt.", parsedId);
            }

            parsed.Add(step);
        }

        return new DesktopJobRequestRead(new DesktopJobRequest(requestId, idleMinutes, parsed), null, parsedId);
    }

    private static (DesktopJobStep? Step, string? Error) ValidateStep(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return (null, "Ein Schritt ist kein JSON-Objekt.");
        }

        var id = Text(Property(element, "id"));
        var script = Text(Property(element, "script"));
        var marker = Text(Property(element, "marker"));

        if (id is null || !StepIdPattern().IsMatch(id))
        {
            return (null, $"Schritt-ID '{id}' ist ungültig (erlaubt: a-z, 0-9, _ und -, höchstens 32 Zeichen).");
        }

        // A plain file name inside the subtask folder: one safe path segment, nothing that Windows
        // would silently collapse into another name.
        if (script is null
            || !SubtaskPaths.IsValidTitle(script)
            || !string.Equals(script, script.Trim(), StringComparison.Ordinal)
            || script.EndsWith('.'))
        {
            return (null, $"Schritt '{id}': script '{script}' muss ein Dateiname im Subtask-Ordner sein.");
        }

        if (!TryInt(Property(element, "timeoutMinutes"), 1, MaxTimeoutMinutes, out var timeout))
        {
            return (null, $"Schritt '{id}': timeoutMinutes muss zwischen 1 und {MaxTimeoutMinutes} liegen.");
        }

        if (marker is null || !Path.IsPathFullyQualified(marker))
        {
            return (null, $"Schritt '{id}': marker muss ein absoluter Pfad sein.");
        }

        return (new DesktopJobStep(id, script, timeout, marker), null);
    }

    private static JsonElement Property(JsonElement owner, string name)
    {
        foreach (var property in owner.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return default;
    }

    private static string? Text(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool TryInt(JsonElement value, int minimum, int maximum, out int result)
    {
        result = 0;

        return value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out result)
            && result >= minimum
            && result <= maximum;
    }
}
