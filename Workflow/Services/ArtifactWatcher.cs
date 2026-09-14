using System.IO;
using System.Security.Cryptography;
using Workflow.Models;

namespace Workflow.Services;

/// <inheritdoc cref="IArtifactWatcher" />
public sealed class ArtifactWatcher : IArtifactWatcher
{
    private readonly CompletionRule _rule;
    private readonly IReadOnlyList<string> _paths;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _pollInterval;
    private readonly Dictionary<string, FileHashResult> _baseline = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _directory;
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly FileSystemWatcher? _watcher;

    private int _disposed;

    /// <summary>Creates the watcher and captures the baseline hashes.</summary>
    /// <param name="rule">Completion rule to apply.</param>
    /// <param name="directory">Directory to watch.</param>
    /// <param name="absolutePaths">Artefact paths the rule applies to.</param>
    /// <param name="debounce">Delay applied after a change notification before re-checking.</param>
    /// <param name="pollInterval">Fallback poll interval for dropped file-system events.</param>
    public ArtifactWatcher(
        CompletionRule rule,
        string directory,
        IReadOnlyList<string> absolutePaths,
        TimeSpan debounce,
        TimeSpan pollInterval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(absolutePaths);

        _rule = rule;
        _paths = absolutePaths;
        _debounce = debounce;
        _pollInterval = pollInterval;
        _directory = directory;

        foreach (var path in _paths)
        {
            _baseline[path] = ComputeHash(path);
        }

        Directory.CreateDirectory(directory);

        _watcher = new FileSystemWatcher(directory)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnFileSystemEvent;
        _watcher.Created += OnFileSystemEvent;
        _watcher.Renamed += OnFileSystemEvent;
        _watcher.Error += OnWatcherError;

        _ = Task.Run(() => PollAsync(_lifetime.Token), _lifetime.Token);

        // The condition may already hold before any event arrives.
        CheckAndSignal();
    }

    /// <inheritdoc />
    public Task WaitAsync(CancellationToken cancellationToken) =>
        _completion.Task.WaitAsync(cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();

        if (_watcher is not null)
        {
            _watcher.Changed -= OnFileSystemEvent;
            _watcher.Created -= OnFileSystemEvent;
            _watcher.Renamed -= OnFileSystemEvent;
            _watcher.Error -= OnWatcherError;
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }

        _lifetime.Dispose();
    }

    private void OnWatcherError(object sender, ErrorEventArgs e) =>
        Fail($"Die Ueberwachung von '{_directory}' ist fehlgeschlagen: {e.GetException().Message}");

    private void Fail(string message)
    {
        if (_completion.Task.IsCompleted)
        {
            return;
        }

        _completion.TrySetException(new ArtifactWatchException(message));
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_debounce, _lifetime.Token);
                CheckAndSignal();
            }
            catch (OperationCanceledException)
            {
                // Watcher disposed while debouncing.
            }
        });

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        // FileSystemWatcher drops events on OneDrive/network/virtualised paths; a missed event
        // would stall the pipeline permanently, so the condition is also polled.
        using var timer = new PeriodicTimer(_pollInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                CheckAndSignal();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private void CheckAndSignal()
    {
        if (_completion.Task.IsCompleted)
        {
            return;
        }

        // A watcher whose directory has gone can never be satisfied. Failing is the documented
        // behaviour (spec section 12.2); continuing to poll would leave the phase Active forever.
        if (_watcher is not null && !Directory.Exists(_directory))
        {
            Fail($"Das Arbeitsverzeichnis '{_directory}' existiert nicht mehr. Die Phase wurde abgebrochen.");
            return;
        }

        var satisfied = _rule switch
        {
            CompletionRule.FilesExist => _paths.All(IsPresentAndNonEmpty),
            CompletionRule.AnyContentChanged => _paths.Any(HasChangedSinceBaseline),
            CompletionRule.AllContentChanged => _paths.All(HasChangedSinceBaseline),
            _ => false,
        };

        if (satisfied)
        {
            _completion.TrySetResult();
        }
    }

    private static bool IsPresentAndNonEmpty(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private bool HasChangedSinceBaseline(string path)
    {
        var current = ComputeHash(path);
        var baseline = _baseline.GetValueOrDefault(path, new FileHashResult(FileHashState.Missing, null));

        // A file we could not read tells us nothing. Reporting a change here would advance the
        // workflow because an editor happened to hold a lock for a few milliseconds.
        if (current.State == FileHashState.Unreadable)
        {
            return false;
        }

        // The baseline itself may have been unreadable at phase start. Re-capture it now that the
        // file can be read, and do not count that first successful read as a change.
        if (baseline.State == FileHashState.Unreadable)
        {
            _baseline[path] = current;
            return false;
        }

        if (current.State != baseline.State)
        {
            return true;
        }

        return current.State == FileHashState.Hash
            && !string.Equals(current.Hash, baseline.Hash, StringComparison.Ordinal);
    }

    private static FileHashResult ComputeHash(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new FileHashResult(FileHashState.Missing, null);
            }

            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            return new FileHashResult(FileHashState.Hash, Convert.ToHexString(SHA256.HashData(stream)));
        }
        catch (IOException)
        {
            // The writer still holds the file exclusively; the next poll will see it.
            return new FileHashResult(FileHashState.Unreadable, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new FileHashResult(FileHashState.Unreadable, null);
        }
    }
}
