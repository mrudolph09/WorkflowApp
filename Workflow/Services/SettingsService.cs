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

        // Root-aware (see WorkingDirectoryPath): persisting "C:" instead of "C:\" would make the
        // restored MRU entry drive-relative on the next launch.
        var normalised = WorkingDirectoryPath.Normalise(directory);

        // Collection<T> has no RemoveAll/RemoveRange (see AppSettings); remove by index instead.
        for (var i = Settings.RecentDirectories.Count - 1; i >= 0; i--)
        {
            if (string.Equals(Settings.RecentDirectories[i], normalised, StringComparison.OrdinalIgnoreCase))
            {
                Settings.RecentDirectories.RemoveAt(i);
            }
        }

        Settings.RecentDirectories.Insert(0, normalised);

        while (Settings.RecentDirectories.Count > MaxRecentDirectories)
        {
            Settings.RecentDirectories.RemoveAt(Settings.RecentDirectories.Count - 1);
        }

        Settings.LastDirectory = normalised;
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
