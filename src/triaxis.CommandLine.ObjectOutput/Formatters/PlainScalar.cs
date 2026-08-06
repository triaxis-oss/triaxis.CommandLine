namespace triaxis.CommandLine.ObjectOutput.Formatters;

using System.Text.RegularExpressions;

/// <summary>
/// Decides whether a string can be emitted as a YAML plain (unquoted) scalar.
/// </summary>
/// <remarks>
/// Block context only — <see cref="YamlWriter"/> never emits flow context except for
/// empty containers, so the flow-only indicators (<c>,</c> <c>[</c> <c>]</c> <c>{</c>
/// <c>}</c>) are not restricted mid-scalar.
///
/// Every uncertain answer resolves to <see langword="false"/>: quoting is always
/// correct, so the failure mode is ugly output rather than a value that reads back
/// as the wrong type. The resolver tables therefore cover the union of YAML 1.1
/// (PyYAML, yq, Psych) and YAML 1.2 core (YamlDotNet) — a token either schema would
/// reinterpret has to be quoted, because the consumer is not known at emit time.
/// </remarks>
static class PlainScalar
{
    /// Characters that may never start a plain scalar.
    private const string LeadingIndicators = ",[]{}#&*!|>'\"%@`";

    /// Characters that only terminate the scalar when a space follows, so <c>--flag</c>,
    /// <c>::1</c> and <c>-value</c> stay unquoted.
    private const string ConditionalLeadingIndicators = "-?:";

    public static bool IsSafe(string s)
    {
        if (s.Length == 0)
        {
            return false;
        }

        // parsers strip whitespace surrounding a plain scalar
        if (IsSpace(s[0]) || IsSpace(s[s.Length - 1]))
        {
            return false;
        }

        if (LeadingIndicators.IndexOf(s[0]) >= 0)
        {
            return false;
        }

        if (ConditionalLeadingIndicators.IndexOf(s[0]) >= 0 && (s.Length == 1 || IsSpace(s[1])))
        {
            return false;
        }

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];

            if (!IsAllowedChar(c))
            {
                return false;
            }

            // ": " ends the key, and so does a ':' at end of line
            if (c == ':' && (i + 1 == s.Length || s[i + 1] == ' '))
            {
                return false;
            }

            // " #" starts a comment
            if (c == '#' && i > 0 && s[i - 1] == ' ')
            {
                return false;
            }
        }

        return !ResolvesAsNonString(s);
    }

    private static bool IsSpace(char c) => c == ' ' || c == '\t';

    private static bool IsAllowedChar(char c)
    {
        // C0 controls (including \n, \r, \t), DEL and C1 controls are YAML non-printables
        if (c < 0x20 || c == 0x7F || (c >= 0x80 && c <= 0x9F))
        {
            return false;
        }

        // a BOM is a non-printable wherever it appears, not just at the start
        return c != '﻿';
    }

    private static readonly HashSet<string> s_keywords = new HashSet<string>(StringComparer.Ordinal)
    {
        // null — 1.1 and 1.2
        "null", "Null", "NULL", "~",
        // bool — 1.2 core
        "true", "True", "TRUE", "false", "False", "FALSE",
        // bool — 1.1 only. PyYAML does not honour bare y/n but Ruby's Psych does.
        "y", "Y", "n", "N",
        "yes", "Yes", "YES", "no", "No", "NO",
        "on", "On", "ON", "off", "Off", "OFF",
        // merge key and the 1.1 "value" type
        "<<", "=",
        // float specials
        ".inf", ".Inf", ".INF", "-.inf", "-.Inf", "-.INF", "+.inf",
        ".nan", ".NaN", ".NAN",
    };

    private static readonly Regex[] s_numericOrDate =
    [
        // decimal int, with the 1.1 '_' separators
        new Regex(@"^[-+]?[0-9][0-9_]*$", RegexOptions.CultureInvariant),
        // 1.1 octal (bare leading zero) and the 1.2 0o form
        new Regex(@"^[-+]?0[0-7_]+$", RegexOptions.CultureInvariant),
        new Regex(@"^[-+]?0o[0-7_]+$", RegexOptions.CultureInvariant),
        new Regex(@"^[-+]?0x[0-9a-fA-F_]+$", RegexOptions.CultureInvariant),
        new Regex(@"^[-+]?0b[01_]+$", RegexOptions.CultureInvariant),
        // 1.1 sexagesimal: 1:30:00 reads back as the integer 5400
        new Regex(@"^[-+]?[0-9][0-9_]*(:[0-5]?[0-9])+(\.[0-9_]*)?$", RegexOptions.CultureInvariant),
        // float. 1.2 accepts an exponent without a fraction ("1e10"), 1.1 does not,
        // so this pattern deliberately matches the looser of the two.
        new Regex(@"^[-+]?(\.[0-9][0-9_]*|[0-9][0-9_]*(\.[0-9_]*)?)([eE][-+]?[0-9]+)?$", RegexOptions.CultureInvariant),
        // 1.1 timestamps
        new Regex(@"^[0-9]{4}-[0-9]{1,2}-[0-9]{1,2}$", RegexOptions.CultureInvariant),
        new Regex(@"^[0-9]{4}-[0-9]{1,2}-[0-9]{1,2}([Tt]|[ \t]+)[0-9]{1,2}:[0-9]{2}:[0-9]{2}(\.[0-9]*)?([ \t]*(Z|[-+][0-9]{1,2}(:?[0-9]{2})?))?$", RegexOptions.CultureInvariant),
    ];

    private static bool ResolvesAsNonString(string s)
    {
        if (s_keywords.Contains(s))
        {
            return true;
        }

        foreach (var rx in s_numericOrDate)
        {
            if (rx.IsMatch(s))
            {
                return true;
            }
        }

        return false;
    }
}
