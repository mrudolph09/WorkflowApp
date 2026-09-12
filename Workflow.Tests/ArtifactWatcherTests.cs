using System.IO;
using Workflow.Models;
using Workflow.Services;

namespace Workflow.Tests;

public sealed class ArtifactWatcherTests : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly string _dir;
    private readonly ArtifactWatcherFactory _factory = new();

    public ArtifactWatcherTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wf-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private IArtifactWatcher Create(CompletionRule rule, params string[] names) =>
        _factory.Create(rule, _dir, names.Select(P).ToList(), Debounce, Poll);

    private static async Task<bool> CompletesAsync(IArtifactWatcher watcher)
    {
        using var cts = new CancellationTokenSource(Timeout);
        try
        {
            await watcher.WaitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    // --- Regressions pinned by the review -------------------------------------------------

    [Fact]
    public async Task AnyContentChanged_DoesNotFireWhileAFileIsExclusivelyLocked()
    {
        await File.WriteAllTextAsync(P("spec.md"), "original");
        using var watcher = Create(CompletionRule.AnyContentChanged, "spec.md");

        // An editor (or the running CLI) holds the file. The bytes have not changed.
        using (new FileStream(P("spec.md"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            // Task.WaitAsync(CancellationToken) throws TaskCanceledException, a subtype of
            // OperationCanceledException; Assert.ThrowsAsync requires an exact type match, so
            // ThrowsAnyAsync is used here - consistent with every other cancellation assertion
            // in this file.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watcher.WaitAsync(cts.Token));
        }
    }

    [Fact]
    public async Task AnyContentChanged_StillFiresAfterTheLockIsReleasedAndTheFileChanges()
    {
        await File.WriteAllTextAsync(P("spec.md"), "original");
        var watcher = Create(CompletionRule.AnyContentChanged, "spec.md");
        try
        {
            using (new FileStream(P("spec.md"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await Task.Delay(300);
            }

            var waiting = CompletesAsync(watcher);
            await File.WriteAllTextAsync(P("spec.md"), "edited");

            Assert.True(await waiting);
        }
        finally
        {
            watcher.Dispose();
        }
    }

    [Fact]
    public async Task DeletingTheWatchedDirectory_FailsTheWatcherInsteadOfPollingForever()
    {
        using var watcher = Create(CompletionRule.FilesExist, "a.md");
        using var waitCts = new CancellationTokenSource(Timeout);
        var waiting = watcher.WaitAsync(waitCts.Token);

        Directory.Delete(_dir, recursive: true);

        var error = await Assert.ThrowsAsync<ArtifactWatchException>(() => waiting);
        Assert.Contains(_dir, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FilesExist_CompletesWhenAllPathsBecomeNonEmpty()
    {
        var watcher = Create(CompletionRule.FilesExist, "a.md", "b.md");
        try
        {
            var waiting = CompletesAsync(watcher);

            await File.WriteAllTextAsync(P("a.md"), "content");
            await File.WriteAllTextAsync(P("b.md"), "content");

            Assert.True(await waiting);
        }
        finally
        {
            watcher.Dispose();
        }
    }

    [Fact]
    public async Task FilesExist_CompletesImmediatelyWhenFilesAreAlreadyThere()
    {
        await File.WriteAllTextAsync(P("a.md"), "content");
        using var watcher = Create(CompletionRule.FilesExist, "a.md");

        Assert.True(await CompletesAsync(watcher));
    }

    [Fact]
    public async Task FilesExist_DoesNotCompleteWhileOnePathIsMissing()
    {
        using var watcher = Create(CompletionRule.FilesExist, "a.md", "b.md");
        await File.WriteAllTextAsync(P("a.md"), "content");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watcher.WaitAsync(cts.Token));
    }

    [Fact]
    public async Task FilesExist_TreatsAZeroByteFileAsNotReady()
    {
        using var watcher = Create(CompletionRule.FilesExist, "a.md");
        await File.WriteAllTextAsync(P("a.md"), string.Empty);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watcher.WaitAsync(cts.Token));
    }

    [Fact]
    public async Task AnyContentChanged_CompletesWhenContentActuallyChanges()
    {
        await File.WriteAllTextAsync(P("a.md"), "before");
        var watcher = Create(CompletionRule.AnyContentChanged, "a.md");
        try
        {
            var waiting = CompletesAsync(watcher);

            await Task.Delay(150);
            await File.WriteAllTextAsync(P("a.md"), "after");

            Assert.True(await waiting);
        }
        finally
        {
            watcher.Dispose();
        }
    }

    [Fact]
    public async Task AnyContentChanged_IgnoresARewriteOfIdenticalContent()
    {
        await File.WriteAllTextAsync(P("a.md"), "same");
        using var watcher = Create(CompletionRule.AnyContentChanged, "a.md");

        await Task.Delay(150);
        await File.WriteAllTextAsync(P("a.md"), "same");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watcher.WaitAsync(cts.Token));
    }

    [Fact]
    public async Task AnyContentChanged_CompletesForAFileThatDidNotExistAtBaseline()
    {
        var watcher = Create(CompletionRule.AnyContentChanged, "a.md");
        try
        {
            var waiting = CompletesAsync(watcher);

            await Task.Delay(150);
            await File.WriteAllTextAsync(P("a.md"), "created");

            Assert.True(await waiting);
        }
        finally
        {
            watcher.Dispose();
        }
    }

    [Fact]
    public async Task Manual_NeverCompletesOnItsOwn()
    {
        using var watcher = Create(CompletionRule.Manual, "a.md");
        await File.WriteAllTextAsync(P("a.md"), "content");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watcher.WaitAsync(cts.Token));
    }

    [Fact]
    public async Task WaitAsync_IsIdempotentAfterCompletion()
    {
        await File.WriteAllTextAsync(P("a.md"), "content");
        using var watcher = Create(CompletionRule.FilesExist, "a.md");

        Assert.True(await CompletesAsync(watcher));
        Assert.True(await CompletesAsync(watcher));
    }
}
