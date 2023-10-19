
using System.Runtime.Serialization;

namespace triaxis.CommandLine.ObjectOutput.Formatters;

class RawObjectFormatterProvider : IObjectFormatterProvider
{
    public ValueTask<IObjectFormatter<T>> CreateFormatterAsync<T>(IObjectDescriptor descriptor, TextWriter output, bool collection)
    {
        return new(new Formatter<T>(output));
    }

    private class Formatter<T> : IObjectFormatter<T>
    {
        private readonly TextWriter _output;

        public Formatter(TextWriter output)
        {
            _output = output;
        }

        public ValueTask OutputElementAsync(T value)
        {
            _output.WriteLine(value?.ToString() ?? "");
            return default;
        }
    }
}
