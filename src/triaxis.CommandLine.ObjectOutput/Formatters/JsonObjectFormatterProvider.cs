using System.Runtime.Serialization;
using System.Text;
using YamlDotNet.Serialization.Schemas;

namespace triaxis.CommandLine.ObjectOutput.Formatters;

class JsonObjectFormatterProvider : IObjectFormatterProvider
{
    public ValueTask<IObjectFormatter<T>> CreateFormatterAsync<T>(IObjectDescriptor descriptor, TextWriter output, bool collection)
    {
        return new(new Formatter<T>(descriptor, output, collection));
    }

    class Formatter<T> : IObjectFormatter<T>, IAsyncDisposable
    {
        private readonly IObjectDescriptor _descriptor;
        private readonly TextWriter _output;
        private readonly bool _collection;
        private string? _separator;

        public Formatter(IObjectDescriptor descriptor, TextWriter output, bool collection)
        {
            _descriptor = descriptor;
            _output = output;
            _collection = collection;

            if (_collection)
            {
                _output.Write('[');
            }
        }

        public ValueTask OutputElementAsync(T value)
        {
            var val = value is null ? null : new Dictionary<string, object?>(_descriptor!.Fields.Ordered().GetValues(value));
            if (_separator is { } sep)
            {
                _output.Write(sep);
            }
            else
            {
                _separator = ",";
            }
            var json = System.Text.Json.JsonSerializer.Serialize(val);
            _output.Write(json);
            return default;
        }

        public ValueTask DisposeAsync()
        {
            if (_collection)
            {
                _output.Write(']');
            }
            _output.WriteLine();
            return default;
        }
    }
}
