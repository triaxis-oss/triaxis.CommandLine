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
        private readonly JsonWriter _writer;
        private bool _needsSeparator;

        public Formatter(IObjectDescriptor descriptor, TextWriter output, bool collection)
        {
            _descriptor = descriptor;
            _output = output;
            _collection = collection;
            _writer = new JsonWriter(output, RuntimeObjectDescriptor.For);

            if (_collection)
            {
                _output.Write('[');
            }
        }

        public ValueTask OutputElementAsync(T value)
        {
            if (_needsSeparator)
            {
                _output.Write(',');
            }
            _needsSeparator = true;

            if (value is null)
            {
                _output.Write("null");
            }
            else
            {
                _writer.WriteElement(_descriptor, value);
            }

            return default;
        }

        public ValueTask DisposeAsync()
        {
            if (_collection)
            {
                _output.Write(']');
            }

            _output.Write('\n');
            return default;
        }
    }
}
