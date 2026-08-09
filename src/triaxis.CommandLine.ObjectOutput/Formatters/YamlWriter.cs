namespace triaxis.CommandLine.ObjectOutput.Formatters;

using System.Collections;
using System.Globalization;
using System.Text;

/// <summary>
/// Block-style YAML 1.2 emitter driven by <see cref="IObjectDescriptor"/>.
/// </summary>
/// <remarks>
/// Flow context is never produced except for empty containers, which is what lets
/// <see cref="PlainScalar"/> reason about block context alone. Line breaks are written
/// as <c>\n</c> explicitly rather than via <see cref="TextWriter.WriteLine()"/>, so the
/// output does not vary with the platform or the writer's <c>NewLine</c>.
/// </remarks>
sealed class YamlWriter
{
    /// Guards against cycles in the object graph; beyond this the value is stringified.
    private const int MaxDepth = 32;

    private readonly TextWriter _output;
    private readonly Func<Type, IObjectDescriptor?> _resolveDescriptor;

    public YamlWriter(TextWriter output, Func<Type, IObjectDescriptor?> resolveDescriptor)
    {
        _output = output;
        _resolveDescriptor = resolveDescriptor;
    }

    /// <summary>
    /// Writes one element, either as a top-level mapping or as an item of the
    /// enclosing sequence.
    /// </summary>
    public void WriteElement(IObjectDescriptor descriptor, object value, bool sequenceItem)
    {
        if (sequenceItem)
        {
            _output.Write("- ");
            WriteFields(descriptor, value, indent: 1, inlineFirstKey: true, depth: 0);
        }
        else
        {
            WriteFields(descriptor, value, indent: 0, inlineFirstKey: false, depth: 0);
        }
    }

    private void WriteFields(IObjectDescriptor descriptor, object target, int indent, bool inlineFirstKey, int depth)
    {
        bool first = true;

        foreach (var field in descriptor.Fields)
        {
            if (!(first && inlineFirstKey))
            {
                WriteIndent(indent);
            }
            first = false;

            WriteKey(field.Name);
            WriteValueAfterKey(field.Accessor.Get(target), indent, depth);
        }

        // a descriptor with no fields would otherwise emit nothing at all, leaving the
        // enclosing "key:" or "-" dangling
        if (first)
        {
            if (!inlineFirstKey)
            {
                WriteIndent(indent);
            }
            _output.Write("{}\n");
        }
    }

    private void WriteMapping(IDictionary map, int indent, bool inlineFirstKey, int depth)
    {
        bool first = true;

        foreach (DictionaryEntry entry in map)
        {
            if (!(first && inlineFirstKey))
            {
                WriteIndent(indent);
            }
            first = false;

            WriteKey(entry.Key?.ToString() ?? "null");
            WriteValueAfterKey(entry.Value, indent, depth);
        }
    }

    private void WriteSequence(IEnumerable seq, int indent, int depth)
    {
        foreach (var item in seq)
        {
            WriteIndent(indent);
            _output.Write('-');

            if (item is null || IsScalar(item) || depth >= MaxDepth)
            {
                WriteScalarAfterPrefix(item, indent);
                continue;
            }

            if (item is IDictionary map)
            {
                if (map.Count == 0)
                {
                    _output.Write(" {}\n");
                }
                else
                {
                    _output.Write(' ');
                    WriteMapping(map, indent + 1, inlineFirstKey: true, depth + 1);
                }
                continue;
            }

            if (item is IEnumerable inner)
            {
                if (IsEmpty(inner))
                {
                    _output.Write(" []\n");
                }
                else
                {
                    _output.Write('\n');
                    WriteSequence(inner, indent + 1, depth + 1);
                }
                continue;
            }

            if (_resolveDescriptor(item.GetType()) is { } nested)
            {
                _output.Write(' ');
                WriteFields(nested, item, indent + 1, inlineFirstKey: true, depth + 1);
                continue;
            }

            WriteScalarAfterPrefix(item, indent);
        }
    }

    private void WriteValueAfterKey(object? value, int indent, int depth)
    {
        if (value is not null && !IsScalar(value) && depth < MaxDepth)
        {
            if (value is IDictionary map)
            {
                if (map.Count == 0)
                {
                    _output.Write(" {}\n");
                }
                else
                {
                    _output.Write('\n');
                    WriteMapping(map, indent + 1, inlineFirstKey: false, depth + 1);
                }
                return;
            }

            if (value is IEnumerable seq)
            {
                if (IsEmpty(seq))
                {
                    _output.Write(" []\n");
                }
                else
                {
                    _output.Write('\n');
                    WriteSequence(seq, indent + 1, depth + 1);
                }
                return;
            }

            if (_resolveDescriptor(value.GetType()) is { } nested)
            {
                _output.Write('\n');
                WriteFields(nested, value, indent + 1, inlineFirstKey: false, depth + 1);
                return;
            }
        }

        WriteScalarAfterPrefix(value, indent);
    }

    /// Writes <c>" &lt;scalar&gt;\n"</c>, or a literal block scalar spanning several lines.
    private void WriteScalarAfterPrefix(object? value, int indent)
    {
        if (value is string s && TryRenderBlock(s, indent, out var header, out var body))
        {
            _output.Write(' ');
            _output.Write(header);
            _output.Write('\n');
            _output.Write(body);
            return;
        }

        _output.Write(' ');
        _output.Write(RenderScalar(value));
        _output.Write('\n');
    }

    private void WriteKey(string key)
    {
        _output.Write(PlainScalar.IsSafe(key) ? key : Quote(key));
        _output.Write(':');
    }

    private void WriteIndent(int indent)
    {
        for (int i = 0; i < indent; i++)
        {
            _output.Write("  ");
        }
    }

    private static bool IsEmpty(IEnumerable e)
    {
        var en = e.GetEnumerator();
        try
        {
            return !en.MoveNext();
        }
        finally
        {
            (en as IDisposable)?.Dispose();
        }
    }

    /// Types rendered as a single scalar rather than walked for members.
    private static bool IsScalar(object v)
        => v is string
        || v is bool
        || v is decimal
        || v is DateTime
        || v is DateTimeOffset
        || v is TimeSpan
        || v is Guid
        || v is Uri
        || v is Enum
        || v.GetType().IsPrimitive;

    public static string RenderScalar(object? value)
    {
        switch (value)
        {
            case null:
                return "null";
            case bool b:
                return b ? "true" : "false";
            case string s:
                return PlainScalar.IsSafe(s) ? s : Quote(s);
            // integral types are unconditionally plain-safe; routing them through the
            // string predicate would quote them as if they were numeric-looking text
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                return ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture);
            case float f:
                return RenderDouble(f);
            case double d:
                return RenderDouble(d);
            case decimal m:
                return EnsureFractional(m.ToString(CultureInfo.InvariantCulture));
            case DateTime dt:
                return dt.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFFK", CultureInfo.InvariantCulture);
            case DateTimeOffset dto:
                return dto.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFFK", CultureInfo.InvariantCulture);
        }

        var text = value is IFormattable fmt
            ? fmt.ToString(null, CultureInfo.InvariantCulture)
            : value.ToString();

        return text is null ? "null"
            : PlainScalar.IsSafe(text) ? text
            : Quote(text);
    }

    private static string RenderDouble(double d)
    {
        if (double.IsNaN(d))
        {
            return ".nan";
        }
        if (double.IsPositiveInfinity(d))
        {
            return ".inf";
        }
        if (double.IsNegativeInfinity(d))
        {
            return "-.inf";
        }

        return EnsureFractional(d.ToString("R", CultureInfo.InvariantCulture));
    }

    /// Keeps a whole-numbered floating point value resolving as a float rather than an
    /// int on the way back, so the emitted type matches the one that was written.
    private static string EnsureFractional(string s)
        => s.IndexOf('.') < 0 && s.IndexOf('E') < 0 && s.IndexOf('e') < 0 ? s + ".0" : s;

    /// <summary>
    /// Renders a literal block scalar, refusing anything whose block form would not
    /// round-trip byte for byte and leaving those to the quoted path.
    /// </summary>
    private static bool TryRenderBlock(string s, int indent, out string header, out string body)
    {
        header = "";
        body = "";

        if (s.IndexOf('\n') < 0)
        {
            return false;
        }

        // block scalars normalise line breaks, so \r\n and lone \r would come back as \n
        if (s.IndexOf('\r') >= 0)
        {
            return false;
        }

        foreach (var c in s)
        {
            if (c != '\n' && (c < 0x20 || c == 0x7F || (c >= 0x80 && c <= 0x9F)))
            {
                return false;
            }
        }

        int trailing = 0;
        while (trailing < s.Length && s[s.Length - 1 - trailing] == '\n')
        {
            trailing++;
        }

        // nothing to anchor the indentation on
        if (trailing == s.Length)
        {
            return false;
        }

        var lines = s.Substring(0, s.Length - trailing).Split('\n');

        // A single content line gains nothing from block form, and YamlDotNet's
        // WithAttemptingUnquotedStringTypeDeserialization resolves such a block by its
        // content rather than its style — so "9\n" would read back as the integer 9,
        // against spec. Leaving one-liners to the quoted path sidesteps that.
        if (lines.Length < 2)
        {
            return false;
        }

        foreach (var line in lines)
        {
            // a leading space would require an explicit indentation indicator, and a
            // trailing one is not reliably preserved
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'
                || line[line.Length - 1] == ' ' || line[line.Length - 1] == '\t'))
            {
                return false;
            }
        }

        header = trailing == 0 ? "|-" : trailing == 1 ? "|" : "|+";

        var pad = new string(' ', (indent + 1) * 2);
        var sb = new StringBuilder();

        foreach (var line in lines)
        {
            if (line.Length > 0)
            {
                sb.Append(pad).Append(line);
            }
            sb.Append('\n');
        }

        // "keep" chomping needs the extra blank lines physically present
        for (int i = 1; i < trailing; i++)
        {
            sb.Append('\n');
        }

        body = sb.ToString();
        return true;
    }

    /// <summary>
    /// Renders a YAML double-quoted scalar, whose escape set is a superset of JSON's — so
    /// one string writer serves both formats.
    /// </summary>
    public static string Quote(string s) => JsonString.Quote(s);
}
