using System.ComponentModel;
using System.Runtime.InteropServices;
using Workflow.Services.Native;

namespace Workflow.Services;

/// <summary>Measures the system-wide CPU load, for the quiet-machine precondition of a desktop job.</summary>
public interface ICpuLoadProbe
{
    /// <summary>Measures the average system-wide CPU load over <paramref name="window"/>.</summary>
    /// <param name="window">How long to measure.</param>
    /// <param name="cancellationToken">Ends the measurement with an <see cref="OperationCanceledException"/>.</param>
    /// <returns>The average load in percent, 0-100.</returns>
    /// <exception cref="InvalidOperationException">
    /// The load could not be measured. Never reported as 0 %: an unknown load is not a quiet machine.
    /// </exception>
    public Task<double> MeasureAsync(TimeSpan window, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="ICpuLoadProbe"/> over GetSystemTimes: two samples, one at each end of the window.
/// Needs no performance-counter permissions, unlike the "Processor" counter category.
/// </summary>
public sealed class SystemCpuLoadProbe : ICpuLoadProbe
{
    /// <inheritdoc />
    public async Task<double> MeasureAsync(TimeSpan window, CancellationToken cancellationToken)
    {
        var before = Sample();
        await Task.Delay(window, cancellationToken);
        var after = Sample();

        // Kernel time includes idle time, so kernel + user is the whole elapsed CPU time.
        var total = (after.Kernel - before.Kernel) + (after.User - before.User);
        var idle = after.Idle - before.Idle;

        if (total <= 0)
        {
            throw new InvalidOperationException("GetSystemTimes lieferte keine verstrichene CPU-Zeit.");
        }

        return Math.Clamp(100.0 * (total - idle) / total, 0, 100);
    }

    private static (long Idle, long Kernel, long User) Sample()
    {
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            throw new InvalidOperationException(
                "GetSystemTimes ist fehlgeschlagen.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        return (idle, kernel, user);
    }
}
