namespace Workflow.Models;

/// <summary>What the person at this machine is doing right now, as far as a desktop job cares.</summary>
/// <param name="IdleTime">Time since the last keyboard or mouse input of the session.</param>
/// <param name="IsLocked">True while the workstation is locked.</param>
/// <param name="IsScreenSaverRunning">True while the screen saver runs.</param>
public readonly record struct UserActivity(TimeSpan IdleTime, bool IsLocked, bool IsScreenSaverRunning);

/// <summary>
/// Decides whether a desktop job may start: the user has been away long enough and the desktop is
/// usable for UI automation. Pure, so the decision is testable without touching the machine.
/// </summary>
public static class IdleWindowPolicy
{
    /// <summary>Reports whether the idle window is open.</summary>
    /// <param name="activity">The current activity.</param>
    /// <param name="idleMinutes">The idle time the job's request asks for.</param>
    /// <returns>
    /// True when the session has been idle for at least <paramref name="idleMinutes"/>, is not
    /// locked and shows no screen saver. A locked or screen-saved desktop counts as away but
    /// unavailable: UI automation cannot drive it.
    /// </returns>
    public static bool IsOpen(UserActivity activity, int idleMinutes) =>
        activity.IdleTime >= TimeSpan.FromMinutes(idleMinutes)
        && !activity.IsLocked
        && !activity.IsScreenSaverRunning;
}
