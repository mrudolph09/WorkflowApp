using Workflow.Models;

namespace Workflow.Services;

/// <summary>Loads and persists the directory MRU list.</summary>
public interface ISettingsService
{
    /// <summary>The in-memory settings.</summary>
    public AppSettings Settings { get; }

    /// <summary>Adds or promotes a directory in the MRU list and records it as the last used one.</summary>
    /// <param name="directory">Absolute directory path. Empty values are ignored.</param>
    public void AddRecentDirectory(string directory);

    /// <summary>Writes the settings to disk atomically.</summary>
    public void Save();
}
