namespace triaxis.CommandLine.ObjectOutput.Formatters;

class DiscardObjectFormatterProvider : IObjectFormatterProvider
{
    public ValueTask<IObjectFormatter<T>> CreateFormatterAsync<T>(IObjectDescriptor descriptor, TextWriter output, bool collection)
    {
        return new(Formatter<T>.Instance);
    }

    private sealed class Formatter<T> : IObjectFormatter<T>
    {
        public static readonly Formatter<T> Instance = new();

        public ValueTask OutputElementAsync(T value)
        {
            return default;
        }
    }
}
