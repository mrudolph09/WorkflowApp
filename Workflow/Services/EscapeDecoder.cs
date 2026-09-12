using System.Globalization;
using System.Text;

namespace Workflow.Services;

/// <summary>Decodes the C-style escapes allowed in a rule's "send" value.</summary>
public static class EscapeDecoder
{
    /// <summary>Decodes \r \n \t \e \\ and \uXXXX. Unknown escapes are left verbatim.</summary>
    /// <param name="raw">The raw value from the rules file.</param>
    /// <returns>The decoded key sequence.</returns>
    public static string Decode(string raw)
    {
        if (string.IsNullOrEmpty(raw) || !raw.Contains('\\', StringComparison.Ordinal))
        {
            return raw ?? string.Empty;
        }

        var builder = new StringBuilder(raw.Length);

        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '\\' || i == raw.Length - 1)
            {
                builder.Append(raw[i]);
                continue;
            }

            var next = raw[i + 1];
            switch (next)
            {
                case 'r': builder.Append('\r'); i++; break;
                case 'n': builder.Append('\n'); i++; break;
                case 't': builder.Append('\t'); i++; break;
                case 'e': builder.Append(''); i++; break;
                case '\\': builder.Append('\\'); i++; break;
                case 'u' when i + 5 < raw.Length
                              && ushort.TryParse(
                                  raw.AsSpan(i + 2, 4),
                                  NumberStyles.HexNumber,
                                  CultureInfo.InvariantCulture,
                                  out var code):
                    builder.Append((char)code);
                    i += 5;
                    break;
                default:
                    builder.Append(raw[i]);
                    break;
            }
        }

        return builder.ToString();
    }
}
