namespace triaxis.CommandLine.ObjectOutput;

public interface IObjectFormatter<T>
{
    ValueTask OutputElementAsync(T value);
}
