using System.Collections.Concurrent;
using System.IO;
using Workflow.Models;

namespace Workflow.Services;

/// <inheritdoc cref="ITaskRecoveryScanner" />
public sealed class TaskRecoveryScanner : ITaskRecoveryScanner
{
    private readonly ISettingsService _settings;
    private readonly ITaskStateStore _store;
    private readonly TimeSpan _maxAge;
    private readonly int _maxTasks;
    private readonly int _maxSubdirectoriesPerRoot;
    private readonly TimeSpan _scanTimeout;

    /// <summary>Creates the scanner.</summary>
    /// <param name="settings">Supplies the directory MRU to scan.</param>
    /// <param name="store">Reads each candidate folder's journal.</param>
    /// <param name="maxAge">How stale a journal may be and still be offered.</param>
    /// <param name="maxTasks">Upper bound on the tabs the scan may cause.</param>
    /// <param name="maxSubdirectoriesPerRoot">Enumeration cap per MRU entry.</param>
    /// <param name="scanTimeout">Ceiling on the whole scan; partial results are kept.</param>
    public TaskRecoveryScanner(
        ISettingsService settings,
        ITaskStateStore store,
        TimeSpan maxAge,
        int maxTasks,
        int maxSubdirectoriesPerRoot,
        TimeSpan scanTimeout)
    {
        _settings = settings;
        _store = store;
        _maxAge = maxAge;
        _maxTasks = maxTasks;
        _maxSubdirectoriesPerRoot = maxSubdirectoriesPerRoot;
        _scanTimeout = scanTimeout;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecoverableTask>> ScanAsync(CancellationToken cancellationToken)
    {
        // Copied HERE, on the CALLER's thread. MainWindowViewModel.InitialiseAsync runs on the
        // dispatcher and the blank tab is already visible and usable, so AddRecentDirectory
        // mutates this very Collection<string> from the UI thread while the scan runs.
        // Enumerating the live collection on the worker faults the whole scan with
        // InvalidOperationException the moment the user picks a directory (SPEC 6.2, D22, R16).
        var roots = _settings.Settings.RecentDirectories.ToList();

        // Appended to as hits are found, so the deadline snapshot below is never empty merely
        // because one later MRU entry is slow.
        var found = new ConcurrentQueue<RecoverableTask>();

        // CA2000: this source is deliberately NOT disposed on this method's scope exit. When the
        // deadline fires, the worker may still be blocked inside Directory.* and would observe a
        // disposed token; it is therefore disposed from the worker's continuation below, which is
        // the only moment at which nothing can still be reading it (SPEC section 6.2).
#pragma warning disable CA2000
        var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
#pragma warning restore CA2000
        budget.CancelAfter(_scanTimeout);

        // CancellationToken.None on purpose: handing the caller's token to Task.Run makes an
        // already-cancelled caller produce a CANCELLED task instead of the partial list this
        // method's contract promises.
        var worker = Task.Run(() => Scan(roots, found, budget.Token), CancellationToken.None);

        // The deadline is enforced HERE, not only inside the worker. Neither Directory.Exists nor
        // the first MoveNext of Directory.EnumerateDirectories can observe a cancellation token -
        // both block inside Win32/SMB - so on a disconnected share the worker can outlive the
        // ceiling by however long the SMB client takes to give up. Waiting out here is what makes
        // the five seconds a guarantee about ScanAsync rather than a hope (SPEC 6.2, D21, R5).
        // No ConfigureAwait(false): Directory.Build.props NoWarns CA2007 precisely because
        // continuations in this application are meant to resume on the UI thread, and the caller
        // (MainWindowViewModel.InitialiseAsync) adds tabs to an ObservableCollection right after
        // awaiting this method.
        await Task.WhenAny(worker, Task.Delay(Timeout.InfiniteTimeSpan, budget.Token));

        // A worker still stuck in that blocking call is abandoned, not awaited: it holds no
        // disposable resource, it only ever enqueues, and nothing reads the queue after this
        // snapshot.
        var snapshot = found.ToArray();

        // Disposed from the worker's continuation rather than here, or an abandoned worker could
        // observe a disposed token.
        _ = worker.ContinueWith(
            _ => budget.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return [.. snapshot.OrderByDescending(t => t.State.UpdatedUtc).Take(_maxTasks)];
    }

    private void Scan(
        IReadOnlyList<string> roots,
        ConcurrentQueue<RecoverableTask> found,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cutoff = DateTimeOffset.UtcNow - _maxAge;

        foreach (var root in roots)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            foreach (var directory in EnumerateTaskFolders(root, cancellationToken))
            {
                if (!seen.Add(directory))
                {
                    continue;
                }

                var candidate = TryBuild(root, directory, cutoff);
                if (candidate is not null)
                {
                    found.Enqueue(candidate);
                }
            }
        }
    }

    // List<string>, not IEnumerable<string>: CA1859 wants the concrete type on a private member,
    // and CA1002 does not apply because nothing here is public.
    private List<string> EnumerateTaskFolders(string root, CancellationToken cancellationToken)
    {
        List<string> directories;

        try
        {
            if (!Directory.Exists(root))
            {
                return [];
            }

            directories = Directory
                .EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
                .TakeWhile(_ => !cancellationToken.IsCancellationRequested)
                .Take(_maxSubdirectoriesPerRoot)
                .ToList();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        return directories;
    }

    private RecoverableTask? TryBuild(string root, string directory, DateTimeOffset cutoff)
    {
        TaskPaths paths;

        try
        {
            paths = new TaskPaths(root, Path.GetFileName(directory));
        }
        catch (ArgumentException)
        {
            // An unusable folder name is simply not one of ours.
            return null;
        }

        var state = _store.TryLoad(paths);
        if (state is null || state.Dismissed)
        {
            return null;
        }

        // Clock skew, or a folder copied from another machine: treat the future as recent rather
        // than hiding a task the user can see is unfinished.
        if (state.UpdatedUtc < cutoff)
        {
            return null;
        }

        // Written back at once when it demotes anything. RecordPhase touches a single entry, so a
        // resumed run would otherwise leave the demoted LATER phases marked Completed on disk and
        // the next crash would skip them - losing the "and every phase after it" half of the rule
        // across one restart (SPEC 6.3.1, D17).
        if (PhaseReconciliation.Reconcile(paths, state))
        {
            _store.ReplacePhases(paths, [.. state.Phases]);
        }

        var resume = PhaseReconciliation.FirstIncomplete(state);
        return resume is null ? null : new RecoverableTask(paths, state, resume.Value);
    }
}
