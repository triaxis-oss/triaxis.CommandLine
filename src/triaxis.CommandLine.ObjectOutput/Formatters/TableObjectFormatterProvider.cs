namespace triaxis.CommandLine.ObjectOutput.Formatters;

using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Options;

class TableObjectFormatterProvider : IObjectFormatterProvider
{
    private readonly TableOutputOptions _options;
    private static int _tableOutputCount;   // TODO: this should be probably tracked per output stream?

    public TableObjectFormatterProvider(IOptions<TableOutputOptions> options)
    {
        _options = options.Value;
    }

    public ValueTask<IObjectFormatter<T>> CreateFormatterAsync<T>(IObjectDescriptor descriptor, TextWriter output, bool collection)
    {
        return new(new Table<T>(descriptor, _options.Wide, output));
    }

    class Column
    {
        private readonly IObjectField _field;
        private readonly string _title;

        public Column(IObjectField field)
        {
            _field = field;
            _title = field.Name.ToTableTitle();
        }

        public int Width { get; set; }

        public Type ValueType => _field.Type;
        public TypeConverter TypeConverter => _field.Converter;

        public string Title => _title;
        public bool PadLeft => ValueType.IsPrimitive && ValueType != typeof(string);

        public string ProcessTitle()
        {
            var title = Title;
            AdjustWidth(Title);
            return title;
        }

        public string ProcessValue(object target)
        {
            object? value = _field.Accessor.Get(target);
            string formatted = FormatValue(value);
            AdjustWidth(formatted);
            return formatted;
        }

        private string FormatValue(object? o)
        {
            if (o == null)
            {
                return "";
            }

            if (o is bool b)
            {
                return b ? "*" : "";
            }

            if (TypeConverter.ConvertToInvariantString(o) is { } s && !string.IsNullOrEmpty(s))
            {
                return s;
            }

            if (o is IEnumerable e && o is not string)
            {
                return string.Join(";", e.Cast<object?>().Select(FormatValue));
            }

            if (o is IConvertible conv)
            {
                return conv.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return o.ToString() ?? "";
        }

        private void AdjustWidth(string s)
        {
            if (Width < s.Length)
            {
                Width = s.Length;
            }
        }
    }

    private static readonly string s_pad = new(' ', 80);

    class Table<T> : IObjectFormatter<T>, IAsyncDisposable
    {
        private readonly Column[] _columns;
        private readonly List<string> _values;
        private readonly TextWriter _output;

        public Table(IObjectDescriptor descriptor, bool wide, TextWriter output)
        {
            _columns = descriptor.Fields
                .Filter(wide ? ObjectFieldVisibility.Extended : ObjectFieldVisibility.Standard)
                .Ordered()
                .Select(f => new Column(f))
                .ToArray();
            _output = output;
            _values = new List<string>();

            foreach (var col in _columns)
            {
                _values.Add(col.ProcessTitle());
            }
        }

        public ValueTask OutputElementAsync(T value)
        {
            foreach (var col in _columns)
            {
                if (value is not null)
                {
                    _values.Add(col.ProcessValue(value));
                }
            }
            return default;
        }

        private void WritePadding(int length)
        {
            while (length > 0)
            {
                int n = length < s_pad.Length ? length : s_pad.Length;
                _output.Write(s_pad.AsSpan(0, n));
                length -= n;
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Increment(ref _tableOutputCount) > 1)
            {
                // separate the tables
                _output.WriteLine();
            }

            int i = 0;
            foreach (var val in _values)
            {
                var col = _columns[i++];
                var padding = col.Width - val.Length;
                bool padLeft = col.PadLeft;

                // write padded value
                if (padding > 0 && padLeft)
                {
                    WritePadding(padding);
                }
                _output.Write(val);
                if (padding > 0 && !padLeft)
                {
                    WritePadding(padding);
                }

                // three spaces between columns, newline after the last one
                if (i == _columns.Length)
                {
                    _output.WriteLine();
                    i = 0;
                }
                else
                {
                    _output.Write("   ");
                }
            }
            return default;
        }
    }
}
