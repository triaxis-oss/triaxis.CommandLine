namespace triaxis.CommandLine.ObjectOutput.Formatters;

using System.Globalization;
using System.Text;

/// <summary>
/// Renders a JSON string literal. Shared by the JSON and YAML emitters — a YAML
/// double-quoted scalar accepts JSON's escape set, so one implementation serves both.
/// </summary>
static class JsonString
{
    /// <remarks>
    /// Non-ASCII is written literally rather than escaped: it is valid in both formats,
    /// far more readable, and still a valid JSON string. An unpaired surrogate is the one
    /// exception — it cannot be encoded as UTF-8 and would reach the reader as U+FFFD, so
    /// it is escaped to stay recoverable.
    /// </remarks>
    public static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');

        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];

            switch (c)
            {
                case '"': sb.Append("\\\""); continue;
                case '\\': sb.Append("\\\\"); continue;
                case '\n': sb.Append("\\n"); continue;
                case '\r': sb.Append("\\r"); continue;
                case '\t': sb.Append("\\t"); continue;
                case '\b': sb.Append("\\b"); continue;
                case '\f': sb.Append("\\f"); continue;
            }

            if (c < 0x20 || c == 0x7F || (c >= 0x80 && c <= 0x9F) || c == '﻿')
            {
                AppendEscape(sb, c);
                continue;
            }

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    sb.Append(c).Append(s[i + 1]);
                    i++;
                }
                else
                {
                    AppendEscape(sb, c);
                }
                continue;
            }

            if (char.IsLowSurrogate(c))
            {
                AppendEscape(sb, c);
                continue;
            }

            sb.Append(c);
        }

        return sb.Append('"').ToString();
    }

    private static void AppendEscape(StringBuilder sb, char c)
        => sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
}
