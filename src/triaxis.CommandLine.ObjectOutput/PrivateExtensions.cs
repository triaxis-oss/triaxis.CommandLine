namespace triaxis.CommandLine.ObjectOutput;

using triaxis.CommandLine.ObjectOutput.Helpers;

static class PrivateExtensions
{
    public static IEnumerable<T> Filter<T>(this IEnumerable<T> fields, ObjectFieldVisibility maxVisibility)
        where T : IObjectField
    {
        var std = fields.Where(f => f.Visibility <= maxVisibility);
        return std.Any() ? std : fields;    // if all fields would be filtered out, return the full set
    }

    public static IEnumerable<T> Ordered<T>(this IEnumerable<T> fields)
        where T : IObjectField
        => fields.OrderBy(f => f.Order).ThenBy(f => f.Visibility).ThenBy(f => f.Name, StringComparer.InvariantCultureIgnoreCase);

    public static IEnumerable<KeyValuePair<string, object?>> GetValues<T>(this IEnumerable<T> fields, object target)
        where T : IObjectField
        => fields.Select(f => new KeyValuePair<string, object?>(f.Name, f.Accessor.Get(target)));

    public static string ToTableTitle(this string s)
    {
        if (s.Length == 0)
        {
            return s;
        }

        var ws = new WordSplitter(s);
        int len = -1;
        while (ws.NextWord() is { } word && !word.IsEmpty)
        {
            len += 1 + word.Length;
        }

        return string.Create(len, s, (span, src) =>
        {
            var ws = new WordSplitter(src);
            while (ws.NextWord() is { } word && !word.IsEmpty)
            {
                len = word.ToUpperInvariant(span);
                span = span[len..];
                if (!span.IsEmpty)
                {
                    span[0] = ' ';
                    span = span[1..];
                }
            }
        });
    }
}
