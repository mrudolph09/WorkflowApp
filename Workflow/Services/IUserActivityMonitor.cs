using Workflow.Models;

namespace Workflow.Services;

/// <summary>Reports what the person at this machine is doing, for desktop jobs.</summary>
public interface IUserActivityMonitor
{
    /// <summary>Reads the idle time, the lock state and the screen saver state right now.</summary>
    /// <returns>The current activity.</returns>
    public UserActivity Read();

    /// <summary>
    /// Starts watching for real keyboard and mouse input while a desktop job step runs. Injected
    /// input - which is what UI automation such as FlaUI produces - is ignored.
    /// </summary>
    /// <returns>The watch; disposing it stops watching.</returns>
    /// <exception cref="InvalidOperationException">
    /// The watch could not be installed. A desktop job must then not start: without it, the user's
    /// return would go unnoticed.
    /// </exception>
    public IRealInputWatch StartRealInputWatch();
}

/// <summary>A running watch for real user input.</summary>
public interface IRealInputWatch : IDisposable
{
    /// <summary>True once real (not injected) input has been seen since the watch started.</summary>
    public bool RealInputDetected { get; }
}

/// <summary>
/// The flag tests of the low-level hook structures, kept pure so the filter that tells the user's
/// input from injected input can be tested without installing a hook.
/// </summary>
public static class LowLevelInputFlags
{
    /// <summary>LLKHF_INJECTED: set for every injected key event, from any integrity level.</summary>
    public const uint KeyboardInjected = 0x10;

    /// <summary>LLMHF_INJECTED: set for every injected mouse event, from any integrity level.</summary>
    public const uint MouseInjected = 0x01;

    /// <summary>Reports whether a KBDLLHOOKSTRUCT.flags value marks an injected event.</summary>
    /// <param name="flags">The flags.</param>
    /// <returns>True for injected input.</returns>
    public static bool IsInjectedKeyboard(uint flags) => (flags & KeyboardInjected) != 0;

    /// <summary>Reports whether an MSLLHOOKSTRUCT.flags value marks an injected event.</summary>
    /// <param name="flags">The flags.</param>
    /// <returns>True for injected input.</returns>
    public static bool IsInjectedMouse(uint flags) => (flags & MouseInjected) != 0;
}
