using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Workflow.Models;

/// <summary>
/// Storage the application owns, outside every task and subtask folder: by default
/// <c>%LOCALAPPDATA%\Workflow</c>. What lives here - decision approvals, authoritative desktop job
/// results - must never be producible by an agent session, which can write anywhere in the
/// tracking repository but is never pointed here.
/// </summary>
public static class AppStorage
{
    private static string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Workflow");

    /// <summary>The storage root. Settable so tests can aim it at a temporary folder.</summary>
    public static string Root
    {
        get => Volatile.Read(ref _root);
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            Volatile.Write(ref _root, value);
        }
    }

    /// <summary>
    /// The storage key of a folder: upper-case hex SHA-256 of its normalised absolute path (full path,
    /// no trailing separator, upper-case - Windows paths are case-insensitive).
    /// </summary>
    /// <param name="path">A folder path.</param>
    /// <returns>64 hex characters.</returns>
    public static string KeyFor(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant();

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    /// <summary>Normalised absolute form of a folder path, as <see cref="KeyFor"/> hashes it (without upper-casing).</summary>
    /// <param name="path">A folder path.</param>
    /// <returns>The full path without a trailing separator.</returns>
    public static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
