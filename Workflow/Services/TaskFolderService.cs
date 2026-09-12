using System.IO;
using Workflow.Models;

namespace Workflow.Services;

/// <inheritdoc cref="ITaskFolderService" />
public sealed class TaskFolderService : ITaskFolderService
{
    private const int MaxCombinedPathLength = 240;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <inheritdoc />
    public TaskNameValidation Validate(string? taskName, string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return TaskNameValidation.Error("Bitte ein gültiges Arbeitsverzeichnis auswählen.");
        }

        if (string.IsNullOrWhiteSpace(taskName))
        {
            return TaskNameValidation.Error("Die Taskbezeichnung darf nicht leer sein.");
        }

        var trimmed = taskName.Trim();

        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return TaskNameValidation.Error(@"Die Taskbezeichnung darf keines der Zeichen \ / : * ? "" < > | enthalten.");
        }

        // Windows silently strips a trailing dot or space, so the folder would not round-trip.
        if (taskName.EndsWith('.') || taskName.EndsWith(' '))
        {
            return TaskNameValidation.Error("Die Taskbezeichnung darf nicht mit einem Punkt oder Leerzeichen enden.");
        }

        var withoutExtension = Path.GetFileNameWithoutExtension(trimmed);
        if (ReservedNames.Contains(withoutExtension))
        {
            return TaskNameValidation.Error($"'{trimmed}' ist ein reservierter Windows-Gerätename.");
        }

        // Root-aware: WorkingDirectoryPath.Normalise keeps "C:\" intact. A plain TrimEnd would
        // produce "C:", making the combined path drive-relative and the length check meaningless.
        var combined = Path.Combine(WorkingDirectoryPath.Normalise(workingDirectory), trimmed);

        if (combined.Length > MaxCombinedPathLength)
        {
            return TaskNameValidation.Error(
                $"Der Pfad wird zu lang ({combined.Length} Zeichen, erlaubt sind {MaxCombinedPathLength}).");
        }

        return TaskNameValidation.Ok;
    }

    /// <inheritdoc />
    public void EnsureCreated(TaskPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        Directory.CreateDirectory(paths.TaskDirectory);
    }

    /// <inheritdoc />
    public bool DirectoryAlreadyExisted(TaskPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return Directory.Exists(paths.TaskDirectory);
    }

    /// <inheritdoc />
    public void Rename(string workingDirectory, string oldName, string newName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);

        var root = WorkingDirectoryPath.Normalise(workingDirectory);
        var source = Path.Combine(root, oldName.Trim());
        var target = Path.Combine(root, newName.Trim());

        if (!Directory.Exists(source) || string.Equals(source, target, StringComparison.Ordinal))
        {
            return;
        }

        // Windows treats a case-only rename as a no-op, so route it through a temporary name.
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            var temporary = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.Move(source, temporary);
            Directory.Move(temporary, target);
            return;
        }

        Directory.Move(source, target);
    }
}
