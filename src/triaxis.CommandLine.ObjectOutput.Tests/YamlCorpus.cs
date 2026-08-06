namespace triaxis.CommandLine.ObjectOutput.Tests;

/// <summary>
/// Strings chosen to break a YAML emitter: every token either schema resolves as a
/// non-string, every indicator character in every position, and the whitespace and
/// line-break shapes that decide between plain, quoted and block form.
/// </summary>
static class YamlCorpus
{
    public static IEnumerable<string> Curated()
    {
        // resolver keywords, in every casing any schema recognises
        string[] words = ["null", "true", "false", "yes", "no", "on", "off", "y", "n", "nan", "inf"];
        foreach (var w in words)
        {
            yield return w;
            yield return w.ToUpperInvariant();
            yield return char.ToUpperInvariant(w[0]) + w.Substring(1);
            yield return w + "x";
            yield return "x" + w;
        }

        foreach (var s in new[] { "~", "<<", "=", ".inf", "-.inf", ".nan", ".NaN", ".INF", "null ", " null" })
        {
            yield return s;
        }

        // numbers in every YAML spelling, including the 1.1-only forms
        foreach (var s in new[]
        {
            "0", "-0", "1", "-1", "+1", "007", "0o17", "0x1F", "0b1010", "0XFF",
            "1_000", "1_000_000", "12_", "_12",
            "1.0", "-1.5", ".5", "5.", "1e10", "1E-10", "-1.2e+3", "1.2.3",
            "1:30", "1:30:00", "01:30:00", "190:20:30", "12:60", "1:2:3.5",
            "0.0", "00", "0777", "1,000", "1 000",
        })
        {
            yield return s;
        }

        foreach (var s in new[]
        {
            "2026-08-06", "2026-8-6", "2026-08-06T12:00:00Z", "2026-08-06T12:00:00.123Z",
            "2026-08-06 12:00:00", "2026-08-06t12:00:00+02:00", "2026-08-06T12:00:00-0700",
            "2026-13-45", "26-08-06", "2026/08/06",
        })
        {
            yield return s;
        }

        // indicator characters at start, middle and end
        const string indicators = "-?:,[]{}#&*!|>'\"%@`\\";
        foreach (var c in indicators)
        {
            yield return c + "value";
            yield return "val" + c + "ue";
            yield return "value" + c;
            yield return c.ToString();
            yield return c + " value";
            yield return "value " + c;
        }

        foreach (var s in new[]
        {
            "key: value", "a:b", "a: b: c", "a #comment", "a#notcomment", "# leading hash",
            "trailing colon:", "colon:space: here", "- dash start", "-dash", "--flag",
            "? question", "!!str coerced", "&anchor", "*alias", "%directive", "@at", "`tick",
        })
        {
            yield return s;
        }

        foreach (var s in new[]
        {
            "", " ", "  ", "\t", " lead", "trail ", " both ", "in ner", "in\tner",
            "double  space", "\tlead tab", "trail tab\t",
        })
        {
            yield return s;
        }

        // line-break shapes, which drive the block-scalar path
        foreach (var s in new[]
        {
            "line1\nline2", "line1\nline2\n", "line1\nline2\n\n", "line1\n\nline3",
            "a\r\nb", "a\rb", "\nleading", "trailing\n", "\n", "\n\n",
            "  indented\nlines", "line\n  indented", "line \ntrailing space",
            "one\ntwo\nthree\nfour", "para one\n\npara two\n",
            "{\n  \"json\": true\n}\n", "SELECT *\nFROM t\nWHERE x = 1;",
        })
        {
            yield return s;
        }

        foreach (var s in new[]
        {
            "bell\a", "null\0char", "esc\u001b[0m", "del\u007f", "c1\u0085next", "bom\ufeffhere",
            "café", "日本語", "emoji 🚀 here", "naïve", "Ω≈ç√", "\ufeffleadingbom",
            "rtl \u202eoverride", "nbsp\u00a0space", "zwsp\u200bhere", "surrogate \U0001F600 pair",
        })
        {
            yield return s;
        }

        // ordinary values, which must survive unquoted
        foreach (var s in new[]
        {
            "hello", "hello world", "Hello World", "some-identifier", "some_identifier",
            "path/to/file", "file.txt", "v1.2.3-beta", "user@example.com", "N/A",
            "CamelCase", "snake_case", "kebab-case", "SCREAMING_CASE",
            "a", "Z", "x86_64", "utf-8", "100%", "50/50", "(parens)", "[brackets]",
            "read+write", "a=b", "3 items", "it's fine", "don't", "\"already quoted\"",
            "C:/Users/test", "https://example.com/path?q=1", "1 + 1 = 2",
            "Lorem ipsum dolor sit amet, consectetur adipiscing elit.",
            "The quick brown fox; jumps over.", "Item (1) of 3", "~/home/path",
            "Running", "Succeeded", "CrashLoopBackOff", "2 days ago", "0/1",
            "sha256:abc123def456", "10.0.0.1", "::1", "2001:db8::1",
            "*.example.com", "80/TCP", "<none>", "<invalid>", "-", "--",
        })
        {
            yield return s;
        }
    }

    /// <summary>
    /// Seeded draws from an alphabet weighted toward the characters that carry meaning
    /// in YAML, which makes a random string far more adversarial than random text.
    /// </summary>
    public static IEnumerable<string> Fuzz(int count)
    {
        char[] alphabet =
        [
            ' ', ' ', '\t', '\n', '\r', ':', ':', '#', '#', '-', '-', '?', ',', '[', ']', '{', '}',
            '&', '*', '!', '|', '>', '\'', '"', '%', '@', '`', '~', '.', '.', '=', '<', '/', '\\',
            '0', '1', '7', '9', 'e', 'E', 'x', 'o', 'b', '_', '+',
            'a', 'n', 'y', 'o', 'f', 's', 'l', 't', 'r', 'u', 'N', 'Y', 'T', 'F',
            '\u00e9', '\u65e5', ' ', ' ', '\u0000', '\u0007', '\u001b', '\u007f', '\ufeff',
        ];

        var rnd = new Random(20260806);

        for (int i = 0; i < count; i++)
        {
            var buf = new char[rnd.Next(0, 13)];
            for (int j = 0; j < buf.Length; j++)
            {
                buf[j] = alphabet[rnd.Next(alphabet.Length)];
            }
            yield return new string(buf);
        }
    }
}
