using System.Diagnostics;
using Workflow.Services.Native;

namespace Workflow.Services;

/// <summary>
/// Keeps the application to one instance per Windows session, through a named mutex. One instance
/// is what makes the app-wide <see cref="ExecutionGate"/> app-wide in fact: a second process would
/// have a gate of its own and could start sessions while the first runs a desktop job.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>The mutex name the application uses.</summary>
    public const string ApplicationMutexName = @"Local\Workflow.SingleInstance";

    private readonly Mutex _mutex;
    private int _disposed;

    private SingleInstanceGuard(Mutex mutex) => _mutex = mutex;

    /// <summary>Takes the named mutex unless another holder already has it.</summary>
    /// <param name="name">The mutex name; tests pass a unique one.</param>
    /// <returns>The guard, which holds the mutex until disposed; or null when it is taken.</returns>
    public static SingleInstanceGuard? TryAcquire(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        if (!createdNew)
        {
            // Not ours: initiallyOwned is ignored for an existing mutex, so there is nothing to release.
            mutex.Dispose();
            return null;
        }

        return new SingleInstanceGuard(mutex);
    }

    /// <summary>
    /// Brings the main window of another instance of this program to the front, best effort: it
    /// does nothing when that instance has no main window yet.
    /// </summary>
    public static void ActivateOtherInstance()
    {
        using var current = Process.GetCurrentProcess();

        foreach (var other in Process.GetProcessesByName(current.ProcessName))
        {
            using (other)
            {
                if (other.Id == current.Id || other.MainWindowHandle == IntPtr.Zero)
                {
                    continue;
                }

                NativeMethods.ShowWindow(other.MainWindowHandle, NativeMethods.SwRestore);
                NativeMethods.SetForegroundWindow(other.MainWindowHandle);
                return;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Released from a thread that does not own it; disposing below still closes the
            // handle, and the mutex disappears with the last handle.
        }

        _mutex.Dispose();
    }
}
