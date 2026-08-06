namespace triaxis.CommandLine.ObjectOutput.Formatters;

class YamlObjectFormatterProvider : IObjectFormatterProvider
{
    public ValueTask<IObjectFormatter<T>> CreateFormatterAsync<T>(IObjectDescriptor descriptor, TextWriter output, bool collection)
    {
        return new(new Formatter<T>(descriptor, output, collection));
    }

    class Formatter<T> : IObjectFormatter<T>
    {
        private readonly IObjectDescriptor _descriptor;
        private readonly TextWriter _output;
        private readonly bool _collection;
        private readonly YamlWriter _writer;

        public Formatter(IObjectDescriptor descriptor, TextWriter output, bool collection)
        {
            _descriptor = descriptor;
            _output = output;
            _collection = collection;
            _writer = new YamlWriter(output, RuntimeObjectDescriptor.For);
        }

        public ValueTask OutputElementAsync(T value)
        {
            if (value is null)
            {
                _output.Write(_collection ? "- null\n" : "null\n");
            }
            else
            {
                _writer.WriteElement(_descriptor, value, _collection);
            }

            return default;
        }
    }
}
