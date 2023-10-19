namespace triaxis.CommandLine.ObjectOutput.Helpers;

using System.Globalization;

public ref struct WordSplitter
{
    private ReadOnlySpan<char> _span;

    private const int MASK_UPPER = 1 << (int)UnicodeCategory.UppercaseLetter;
    private const int MASK_LOWER = 1 << (int)UnicodeCategory.LowercaseLetter;
    private const int MASK_DIGIT = 1 << (int)UnicodeCategory.DecimalDigitNumber;
    private const int MASK_ANY = MASK_UPPER | MASK_LOWER | MASK_DIGIT;

    private static int GetMask(char c) => 1 << (int)char.GetUnicodeCategory(c);

    public WordSplitter(ReadOnlySpan<char> span)
    {
        // pre-trim the word
        _span = TrimEnd(TrimStart(span));
    }

    private static ReadOnlySpan<char> TrimStart(ReadOnlySpan<char> span)
    {
        while (span.Length > 0 && (GetMask(span[0]) & MASK_ANY) == 0)
        {
            span = span[1..];
        }
        return span;
    }

    private static ReadOnlySpan<char> TrimEnd(ReadOnlySpan<char> span)
    {
        while (span.Length > 0 && (GetMask(span[^1]) & MASK_ANY) == 0)
        {
            span = span[..^1];
        }
        return span;
    }

    public ReadOnlySpan<char> NextWord()
    {
        var res = _span;

        if (res.IsEmpty)
        {
            // end of sentence
            return default;
        }

        if (res.Length == 1)
        {
            // a single letter is always its own word, we need at least two to work with
            _span = default;
            return res;
        }

        var first = char.GetUnicodeCategory(res[0]);
        if (first == UnicodeCategory.UppercaseLetter)
        {
            // when a word starts with uppercase, look at the next character to determine the split point
            switch (char.GetUnicodeCategory(res[1]))
            {
                case UnicodeCategory.UppercaseLetter:
                    // run up to next non-uppercase character, then return the last one if it's a lowercase letter
                    for (int i = 2; i < res.Length; i++)
                    {
                        var cat = char.GetUnicodeCategory(res[i]);
                        if (cat == UnicodeCategory.LowercaseLetter)
                        {
                            res = res[..(i - 1)];
                            break;
                        }
                        if (cat != UnicodeCategory.UppercaseLetter)
                        {
                            res = res[..i];
                            break;
                        }
                    }
                    break;

                case UnicodeCategory.LowercaseLetter:
                    // find the end of lowercase letter run
                    for (int i = 1; i < res.Length; i++)
                    {
                        if (char.GetUnicodeCategory(res[i]) != UnicodeCategory.LowercaseLetter)
                        {
                            res = res[..i];
                            break;
                        }
                    }
                    break;

                default:
                    // cut off at non-letter
                    res = res[..1];
                    break;
            }
        }
        else
        {
            // take a run of characters of the same kind
            for (int i = 1; i < res.Length; i++)
            {
                if (char.GetUnicodeCategory(res[i]) != first)
                {
                    res = res[..i];
                    break;
                }
            }
        }

        _span = TrimStart(_span[res.Length..]);
        return res;
    }
}
