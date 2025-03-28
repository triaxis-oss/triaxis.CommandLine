namespace triaxis.CommandLine.ObjectOutput.Formatters;

using Microsoft.Extensions.Options;
using YamlDotNet.Serialization;

using IObjectDescriptor = ObjectOutput.IObjectDescriptor;

class YamlObjectFormatterProvider : IObjectFormatterProvider
{
    private static Serializer? s_serializer;
    private readonly ISerializer _serializer;

    public YamlObjectFormatterProvider(IEnumerable<IConfigureOptions<SerializerBuilder>> configuration)
    {
        SerializerBuilder? sb = null;

        foreach (var cfg in configuration)
        {
            cfg.Configure(sb ??= new());
        }

        _serializer = sb?.Build() ?? (s_serializer ??= new());
    }

    public ValueTask<IObjectFormatter<T>> CreateFormatterAsync<T>(IObjectDescriptor descriptor, TextWriter output, bool collection)
    {
        return new(new Formatter<T>(_serializer, descriptor, output, collection));
    }

    class Formatter<T> : IObjectFormatter<T>
    {
        private readonly ISerializer _serializer;
        private readonly IObjectDescriptor _descriptor;
        private readonly TextWriter _output;
        private readonly bool _collection;

        public Formatter(ISerializer serializer, IObjectDescriptor descriptor, TextWriter output, bool collection)
        {
            _serializer = serializer;
            _descriptor = descriptor;
            _output = output;
            _collection = collection;
        }

        public ValueTask OutputElementAsync(T value)
        {
            var val = value is null ? null : _descriptor.Fields.GetValuesDictionary(value);
            _serializer.Serialize(_output, _collection ? new[] { val } : val);
            return default;
        }
    }
}
