namespace Workflow.Services;

/// <summary>
/// Splits a decoded auto-answer "send" value into discrete key presses.
/// </summary>
/// <remarks>
/// Ink-based TUIs (Claude Code, Codex) parse each stdin chunk as ONE keypress. A single write
/// of "&lt;Down&gt;&lt;Enter&gt;" therefore arrives as an unknown key and is silently dropped,
/// which is how the bypass-permissions dialog ended up answered with its default "No, exit".
/// The orchestrator writes each element of the returned list separately, with a short pause.
/// </remarks>
public static class KeySequence
{
    private const char Escape = '';

    /// <summary>Splits <paramref name="keys"/> into one entry per key press.</summary>
    /// <param name="keys">The decoded key text, for example ESC [ B followed by CR.</param>
    /// <returns>
    /// CSI sequences (ESC [ ... final byte) and SS3 sequences (ESC O x) are kept together;
    /// every other character is its own key.
    /// </returns>
    public static IReadOnlyList<string> Split(string keys)
    {
        if (string.IsNullOrEmpty(keys))
        {
            return [];
        }

        var result = new List<string>();
        var i = 0;

        while (i < keys.Length)
        {
            if (keys[i] != Escape || i + 1 >= keys.Length)
            {
                result.Add(keys[i].ToString());
                i++;
                continue;
            }

            var start = i;
            var introducer = keys[i + 1];

            if (introducer == '[')
            {
                // CSI: parameter and intermediate bytes 0x20-0x3F, then one final byte 0x40-0x7E.
                i += 2;
                while (i < keys.Length && keys[i] >= ' ' && keys[i] <= '?')
                {
                    i++;
                }

                if (i < keys.Length)
                {
                    i++;
                }
            }
            else if (introducer == 'O')
            {
                // SS3: exactly one byte follows.
                i = Math.Min(keys.Length, i + 3);
            }
            else
            {
                // Alt+key style: ESC followed by a single character.
                i += 2;
            }

            result.Add(keys[start..i]);
        }

        return result;
    }
}
