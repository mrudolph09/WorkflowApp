namespace Workflow.Models;

/// <summary>How a hash attempt ended.</summary>
public enum FileHashState
{
    /// <summary>The file was read end to end and <see cref="FileHashResult.Hash"/> is set.</summary>
    Hash,

    /// <summary>The file does not exist.</summary>
    Missing,

    /// <summary>The file exists but could not be read right now (another process holds it).</summary>
    Unreadable,
}

/// <summary>The outcome of hashing one artefact.</summary>
/// <param name="State">How the attempt ended.</param>
/// <param name="Hash">The SHA-256 hex string when <paramref name="State"/> is <see cref="FileHashState.Hash"/>; otherwise null.</param>
public readonly record struct FileHashResult(FileHashState State, string? Hash);
