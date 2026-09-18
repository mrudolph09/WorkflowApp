using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using Workflow.Models;

namespace Workflow.Services;

/// <inheritdoc cref="ISettingsService" />
public sealed class SettingsService : ISettingsService
{
    private const int MaxRecentDirectories = 15;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;

    /// <summary>Creates the service and loads the file if it exists.</summary>
    /// <param name="path">Full path of settings.json.</param>
    public SettingsService(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _path = path;
        Settings = Load(path);
    }

    /// <summary>The default settings location, %APPDATA%\Workflow\settings.json.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Workflow",
        "settings.json");

    /// <inheritdoc />
    public AppSettings Settings { get; }

    /// <inheritdoc />
    public void AddRecentDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Settings.LastDirectory = Promote(Settings.RecentDirectories, directory, MaxRecentDirectories);
    }

    /// <inheritdoc />
    public void AddRecentWorkflowDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Settings.LastWorkflowDirectory =
            Promote(Settings.RecentWorkflowDirectories, directory, MaxRecentDirectories);
    }

    /// <inheritdoc />
    public void Save()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = _path + ".tmp";

        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(Settings, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (IOException)
        {
            // Settings are a convenience; losing them must never take the application down.
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                    // Nothing further can be done.
                }
            }
        }
    }

    /// <summary>
    /// Normalises <paramref name="directory" />, removes case-insensitive duplicates, puts it at
    /// the front of <paramref name="directories" /> and caps the list.
    /// </summary>
    /// <param name="directories">The MRU list to mutate.</param>
    /// <param name="directory">Non-empty raw directory path.</param>
    /// <param name="maximum">Maximum number of entries kept.</param>
    /// <returns>The normalised path that was promoted.</returns>
    /// <remarks>
    /// Shared by both MRU lists so the working-directory and tracking-directory histories can never
    /// drift apart in normalisation, de-duplication or capping behaviour.
    /// </remarks>
    private static string Promote(Collection<string> directories, string directory, int maximum)
    {
        // Root-aware (see WorkingDirectoryPath): persisting "C:" instead of "C:\" would make the
        // restored MRU entry drive-relative on the next launch.
        var normalised = WorkingDirectoryPath.Normalise(directory);

        // Collection<T> has no RemoveAll/RemoveRange (see AppSettings); remove by index instead.
        for (var i = directories.Count - 1; i >= 0; i--)
        {
            if (string.Equals(directories[i], normalised, StringComparison.OrdinalIgnoreCase))
            {
                directories.RemoveAt(i);
            }
        }

        directories.Insert(0, normalised);

        while (directories.Count > maximum)
        {
            directories.RemoveAt(directories.Count - 1);
        }

        return normalised;
    }

    private static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
        catch (IOException)
        {
            return new AppSettings();
        }
    }
}
