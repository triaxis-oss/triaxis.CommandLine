namespace triaxis.CommandLine.ObjectOutput.Formatters;

class YamlObjectFormatterProvider : IObjectFormatterProvider
{
    private static YamlDotNet.Serialization.Serializer s_serializer = new();

    public ValueTask<IObjectFormatter<T>> CreateFormatterAsync<T>(IObjectDescriptor descriptor, TextWriter output, bool collection)
    {
        return new(new Formatter<T>(descriptor, output, collection));
    }

    class Formatter<T> : IObjectFormatter<T>
    {
        private readonly IObjectDescriptor _descriptor;
        private readonly TextWriter _output;
        private readonly bool _collection;

        public Formatter(IObjectDescriptor descriptor, TextWriter output, bool collection)
        {
            _descriptor = descriptor;
            _output = output;
            _collection = collection;
        }

        public ValueTask OutputElementAsync(T value)
        {
            var val = value is null ? null : new Dictionary<string, object?>(_descriptor.Fields.GetValues(value));
            s_serializer.Serialize(_output, _collection ? new[] { val } : val);
            return default;
        }
    }
}
