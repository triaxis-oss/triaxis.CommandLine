namespace triaxis.CommandLine.ObjectOutput;

public interface IObjectFormatterProvider
{
    ValueTask<IObjectFormatter<T>> CreateFormatterAsync<T>(IObjectDescriptor descriptor, TextWriter output, bool collection);
}
