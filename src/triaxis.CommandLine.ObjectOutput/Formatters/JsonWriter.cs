namespace triaxis.CommandLine.ObjectOutput.Formatters;

using System.Collections;
using System.Globalization;

/// <summary>
/// Compact JSON emitter driven by <see cref="IObjectDescriptor"/>.
/// </summary>
/// <remarks>
/// Sharing the descriptor walk with the YAML emitter is what keeps the two formats
/// agreeing below the top level: previously JSON handed a flat dictionary to a serializer
/// that then resolved nested shape on its own, so <c>[ObjectOutput]</c> ordering applied
/// only to the root object.
/// </remarks>
sealed class JsonWriter
{
    /// Guards against cycles in the object graph; beyond this the value is stringified.
    private const int MaxDepth = 32;

    private readonly TextWriter _output;
    private readonly Func<Type, IObjectDescriptor?> _resolveDescriptor;

    public JsonWriter(TextWriter output, Func<Type, IObjectDescriptor?> resolveDescriptor)
    {
        _output = output;
        _resolveDescriptor = resolveDescriptor;
    }

    public void WriteElement(IObjectDescriptor descriptor, object value)
        => WriteFields(descriptor, value, depth: 0);

    private void WriteFields(IObjectDescriptor descriptor, object target, int depth)
    {
        _output.Write('{');
        bool first = true;

        foreach (var field in descriptor.Fields)
        {
            if (!first)
            {
                _output.Write(',');
            }
            first = false;

            _output.Write(JsonString.Quote(field.Name));
            _output.Write(':');
            WriteValue(field.Accessor.Get(target), depth + 1);
        }

        _output.Write('}');
    }

    public void WriteValue(object? value, int depth)
    {
        if (value is null)
        {
            _output.Write("null");
            return;
        }

        if (!IsScalar(value) && depth < MaxDepth)
        {
            if (value is IDictionary map)
            {
                _output.Write('{');
                bool first = true;
                foreach (DictionaryEntry entry in map)
                {
                    if (!first)
                    {
                        _output.Write(',');
                    }
                    first = false;

                    _output.Write(JsonString.Quote(entry.Key?.ToString() ?? "null"));
                    _output.Write(':');
                    WriteValue(entry.Value, depth + 1);
                }
                _output.Write('}');
                return;
            }

            if (value is IEnumerable seq)
            {
                _output.Write('[');
                bool first = true;
                foreach (var item in seq)
                {
                    if (!first)
                    {
                        _output.Write(',');
                    }
                    first = false;

                    WriteValue(item, depth + 1);
                }
                _output.Write(']');
                return;
            }

            if (_resolveDescriptor(value.GetType()) is { } nested)
            {
                WriteFields(nested, value, depth);
                return;
            }
        }

        _output.Write(RenderScalar(value));
    }

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

    private static string RenderScalar(object value)
    {
        switch (value)
        {
            case bool b:
                return b ? "true" : "false";
            case string s:
                return JsonString.Quote(s);
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                return ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture);
            case decimal m:
                return m.ToString(CultureInfo.InvariantCulture);
            case float f:
                return RenderDouble(f);
            case double d:
                return RenderDouble(d);
            case DateTime dt:
                return JsonString.Quote(dt.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFFK", CultureInfo.InvariantCulture));
            case DateTimeOffset dto:
                return JsonString.Quote(dto.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFFK", CultureInfo.InvariantCulture));
        }

        var text = value is IFormattable fmt
            ? fmt.ToString(null, CultureInfo.InvariantCulture)
            : value.ToString();

        return text is null ? "null" : JsonString.Quote(text);
    }

    private static string RenderDouble(double d)
    {
        // JSON has no literal for these, and a bare NaN would not parse. A string keeps
        // the value visible instead of silently turning it into null.
        if (double.IsNaN(d))
        {
            return "\"NaN\"";
        }
        if (double.IsPositiveInfinity(d))
        {
            return "\"Infinity\"";
        }
        if (double.IsNegativeInfinity(d))
        {
            return "\"-Infinity\"";
        }

        return d.ToString("R", CultureInfo.InvariantCulture);
    }
}
