namespace Workflow.Models;

/// <summary>
/// One gate step a finished task or subtask left for a human, or a source whose open items could
/// not be read.
/// </summary>
/// <param name="Source"><c>Implementierung</c> for the task's done file, otherwise the subtask title.</param>
/// <param name="Text">What a human has to do or check, as the agent wrote it, or why the source is unreadable.</param>
/// <param name="IsReadError">
/// True when the source could not be evaluated. Such an item stands in for an unknown number of open
/// items, so an unreadable file is never shown as "nothing open".
/// </param>
public sealed record OpenGateItem(string Source, string Text, bool IsReadError);
