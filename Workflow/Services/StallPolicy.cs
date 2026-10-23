namespace Workflow.Services;

/// <summary>
/// When an unattended session counts as stalled, and how often the watchdog looks (B6). A session
/// whose terminal has produced no output for <see cref="StallAfter"/> is nudged once, and killed on
/// the second stall.
/// </summary>
/// <param name="StallAfter">Silence after which a session counts as stalled.</param>
/// <param name="PollInterval">How often the watchdog compares the terminal's last output with the clock.</param>
public sealed record StallPolicy(TimeSpan StallAfter, TimeSpan PollInterval)
{
    /// <summary>Fifteen silent minutes, checked every thirty seconds.</summary>
    public static StallPolicy Default { get; } = new(TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(30));
}
