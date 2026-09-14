using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Workflow.Models;

namespace Workflow.Services;

/// <inheritdoc cref="ITaskStateStore" />
public sealed class TaskStateStore : ITaskStateStore
{
    // Enums as strings and camelCase property names, so the file on disk is exactly the shape
    // SPEC section 5.2 documents and a future reordering of WorkflowPhase cannot silently
    // reinterpret an old journal.
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    // No JsonStringEnumConverter on the READ path: see the remark on TryLoad.
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <inheritdoc />
    public TaskState? TryLoad(TaskPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        try
        {
            if (!File.Exists(paths.StateAbsolute))
            {
                return null;
            }

            // Deliberately NOT Deserialize<TaskState>. JsonStringEnumConverter throws
            // JsonException on a string it cannot map to an enum member, so ONE hand-edited
            // "phase": "Nonsense" would make this method return null and hide the whole task -
            // and the normaliser below would never run. The DTO keeps phase and status as
            // strings so an unmappable entry can be dropped on its own (SPEC 5.2, R3, D19).
            var dto = JsonSerializer.Deserialize<TaskStateDto>(
                File.ReadAllText(paths.StateAbsolute), ReadOptions);

            if (dto is null || dto.Version > TaskState.CurrentVersion)
            {
                return null;
            }

            return new TaskState
            {
                // A journal written before the version field existed reads as 0; treat it as this
                // build's schema rather than as a downgrade.
                Version = dto.Version > 0 ? dto.Version : TaskState.CurrentVersion,
                TaskDescription = dto.TaskDescription ?? string.Empty,
                CreatedUtc = dto.CreatedUtc,
                UpdatedUtc = dto.UpdatedUtc,
                Dismissed = dto.Dismissed,
                Phases = Normalise(dto.Phases?.Select(ToPhaseState)),
            };
        }
        catch (JsonException)
        {
            return null;
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

    /// <inheritdoc />
    public void SaveDescription(TaskPaths paths, string taskDescription)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var state = TryLoad(paths) ?? CreateEmpty();
        state.TaskDescription = taskDescription ?? string.Empty;
        state.Dismissed = false;
        Save(paths, state);
    }

    /// <inheritdoc />
    public void RecordPhase(TaskPaths paths, WorkflowPhase phase, PhaseStatus status)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var state = TryLoad(paths) ?? CreateEmpty();
        var index = (int)phase;

        if (index < 0 || index >= state.Phases.Count)
        {
            return;
        }

        state.Phases[index] = new TaskPhaseState(
            phase,
            status,
            status == PhaseStatus.Completed ? DateTimeOffset.UtcNow : null);

        Save(paths, state);
    }

    /// <inheritdoc />
    public void ReplacePhases(TaskPaths paths, IReadOnlyList<TaskPhaseState> phases)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(phases);

        var state = TryLoad(paths);
        if (state is null)
        {
            // Nothing on disk to correct. Creating a journal here would invent a task that the
            // scan has never seen.
            return;
        }

        state.Phases = Normalise(phases);
        Save(paths, state, stampUpdated: false);
    }

    /// <inheritdoc />
    public void SetDismissed(TaskPaths paths, bool dismissed)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var state = TryLoad(paths);
        if (state is null)
        {
            return;
        }

        state.Dismissed = dismissed;
        Save(paths, state);
    }

    private static TaskState CreateEmpty() => new()
    {
        CreatedUtc = DateTimeOffset.UtcNow,
        Phases = Normalise([]),
    };

    // One journal entry, as it appears on disk. Enum.TryParse also accepts a NUMERIC string, so
    // every parse is followed by Enum.IsDefined - otherwise "phase": "7" would sail through as
    // (WorkflowPhase)7 and blow up the (int)-indexed callers downstream.
    private static TaskPhaseState? ToPhaseState(TaskPhaseStateDto? entry)
    {
        if (entry is null
            || !Enum.TryParse<WorkflowPhase>(entry.Phase, ignoreCase: true, out var phase)
            || !Enum.IsDefined(phase)
            || !Enum.TryParse<PhaseStatus>(entry.Status, ignoreCase: true, out var status)
            || !Enum.IsDefined(status))
        {
            // Unknown or misspelled name: drop this entry and keep the rest of the journal.
            return null;
        }

        return new TaskPhaseState(phase, status, entry.CompletedUtc);
    }

    // A journal may be hand-edited, truncated, or written by an older build. Everything
    // downstream indexes Phases by (int)WorkflowPhase, so the array is rebuilt to exactly the
    // four catalogue phases in order before anyone sees it.
    private static Collection<TaskPhaseState> Normalise(IEnumerable<TaskPhaseState?>? existing)
    {
        var byPhase = new Dictionary<WorkflowPhase, TaskPhaseState>();

        foreach (var entry in existing ?? [])
        {
            if (entry is not null)
            {
                byPhase[entry.Phase] = entry;
            }
        }

        var result = new Collection<TaskPhaseState>();

        foreach (var definition in PhaseCatalog.All)
        {
            result.Add(byPhase.TryGetValue(definition.Phase, out var found)
                ? found
                : new TaskPhaseState(definition.Phase, PhaseStatus.Pending, null));
        }

        return result;
    }

    // Mirrors SettingsService.Save: a journal write that fails must never take down a live
    // workflow run. Only recovery is degraded.
    private static void Save(TaskPaths paths, TaskState state, bool stampUpdated = true)
    {
        if (stampUpdated || state.UpdatedUtc == default)
        {
            state.UpdatedUtc = DateTimeOffset.UtcNow;
        }

        if (state.CreatedUtc == default)
        {
            state.CreatedUtc = state.UpdatedUtc;
        }

        var temporary = paths.StateAbsolute + ".tmp";

        try
        {
            Directory.CreateDirectory(paths.TaskDirectory);
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, WriteOptions));
            File.Move(temporary, paths.StateAbsolute, overwrite: true);
        }
        catch (IOException)
        {
            // See the remark above.
        }
        catch (UnauthorizedAccessException)
        {
            // See the remark above.
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Nothing further can be done.
        }
        catch (UnauthorizedAccessException)
        {
            // Nothing further can be done.
        }
    }

    // The READ shape. Deliberately not TaskState: every field is as permissive as the file can
    // be, so a single bad entry costs itself and nothing else (SPEC 5.2).
    private sealed class TaskStateDto
    {
        public int Version { get; set; }

        public string? TaskDescription { get; set; }

        public DateTimeOffset CreatedUtc { get; set; }

        public DateTimeOffset UpdatedUtc { get; set; }

        public bool Dismissed { get; set; }

        public List<TaskPhaseStateDto?>? Phases { get; set; }
    }

    private sealed class TaskPhaseStateDto
    {
        public string? Phase { get; set; }

        public string? Status { get; set; }

        public DateTimeOffset? CompletedUtc { get; set; }
    }
}
