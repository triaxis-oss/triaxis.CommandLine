namespace triaxis.CommandLine.ObjectOutput;

using System.Diagnostics;
using System.Text;
using triaxis.CommandLine.ObjectOutput.Helpers;

static class PrivateExtensions
{
    public static IEnumerable<T> Filter<T>(this IEnumerable<T> fields, ObjectFieldVisibility maxVisibility)
        where T : IObjectField
    {
        var std = fields.Where(f => f.Visibility <= maxVisibility);
        return std.Any() ? std : fields;    // if all fields would be filtered out, return the full set
    }

    public static T[] Ordered<T>(this IEnumerable<T> fields)
        where T : IObjectField
    {
        List<IObjectFieldOrdering?>? before = null, after = null;
        var main = new List<T>();

        foreach (var f in fields)
        {
            if (f is IObjectFieldOrdering ordered)
            {
                if (ordered.Before is not null)
                {
                    (before ??= new()).Add(ordered);
                    continue;
                }
                else if (ordered.After is not null)
                {
                    (after ??= new()).Add(ordered);
                    continue;
                }
            }
            main.Add(f);
        }

        if (before is null && after is null)
        {
            return main.ToArray();
        }

        var res = new T[main.Count + (before?.Count ?? 0) + (after?.Count ?? 0)];
        int i = 0;

        void AddOrdered(List<IObjectFieldOrdering?>? list, Predicate<IObjectFieldOrdering?> predicate)
        {
            if (list is null)
            {
                return;
            }

            int i = 0;
            while ((i = list.FindIndex(i, predicate)) >= 0)
            {
                var f = (T)list[i]!;
                list[i] = null;
                Add(f);
            }
        }

        void Add(T f)
        {
            AddOrdered(before, of => of?.Before == f.Name);
            res[i++] = f;
            AddOrdered(after, of => of?.After == f.Name);
        }

        foreach (var f in main)
        {
            Add(f);
        }

        // flush remaining ordered
        AddOrdered(before, of => of is not null);
        AddOrdered(after, of => of is not null);

        Debug.Assert(i == res.Length);
        return res;
    }

    public static IEnumerable<KeyValuePair<string, object?>> GetValues<T>(this IEnumerable<T> fields, object target)
        where T : IObjectField
        => fields.Select(f => new KeyValuePair<string, object?>(f.Name, f.Accessor.Get(target)));

    public static IDictionary<string, object?> GetValuesDictionary<T>(this IEnumerable<T> fields, object target)
        where T : IObjectField
    {
#if NETSTANDARD2_0
        var dic = new Dictionary<string, object?>();
        foreach (var kvp in fields.GetValues(target))
        {
            dic.Add(kvp.Key, kvp.Value);
        }
        return dic;
#else
        return new Dictionary<string, object?>(fields.GetValues(target));
#endif
    }

    public static string ToTableTitle(this string s)
    {
        if (s.Length == 0)
        {
            return s;
        }

        var ws = new WordSplitter(s.AsSpan());
        int len = -1;
        while (ws.NextWord() is { } word && !word.IsEmpty)
        {
            len += 1 + word.Length;
        }

#if NETSTANDARD2_0
        var sb = new StringBuilder(len);
        while (ws.NextWord() is {} word && !word.IsEmpty)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }
            sb.Append(word.ToString().ToUpperInvariant());
        }

        return sb.ToString();
#else
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
#endif
    }
}
