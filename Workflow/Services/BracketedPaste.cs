namespace Workflow.Services;

/// <summary>Builds a bracketed-paste block (DECSET 2004).</summary>
public static class BracketedPaste
{
    private const string Begin = "[200~";
    private const string End = "[201~";

    /// <summary>
    /// Wraps multi-line text so a TUI input box treats it as a single paste instead of
    /// submitting on the first newline.
    /// </summary>
    /// <param name="body">The text to paste. All newlines are normalised to CR.</param>
    /// <returns>The bracketed-paste block. The submitting CR is sent separately.</returns>
    public static string Wrap(string body)
    {
        var normalised = (body ?? string.Empty)
            .Replace("\r\n", "\r", StringComparison.Ordinal)
            .Replace('\n', '\r');

        return Begin + normalised + End;
    }
}
