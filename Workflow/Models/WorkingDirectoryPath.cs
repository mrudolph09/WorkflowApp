using System.IO;

namespace Workflow.Models;

/// <summary>Normalises a user-selected working-directory path.</summary>
public static class WorkingDirectoryPath
{
    /// <summary>
    /// Trims surrounding whitespace and a trailing directory separator, but never turns a drive
    /// root into a drive-relative path.
    /// </summary>
    /// <param name="directory">Raw path, typically straight from the folder picker.</param>
    /// <returns>The normalised path.</returns>
    /// <remarks>
    /// <c>TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)</c> is wrong here:
    /// it turns <c>C:\</c> into <c>C:</c>, which Windows resolves against the drive's current
    /// directory. <c>Path.Combine("C:", "task")</c> is then <c>C:task</c>, and the emitted
    /// <c>cd "C:"</c> does not change to the root. <c>Path.TrimEndingDirectorySeparator</c>
    /// leaves roots alone by design.
    /// </remarks>
    public static string Normalise(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        return Path.TrimEndingDirectorySeparator(directory.Trim());
    }
}
