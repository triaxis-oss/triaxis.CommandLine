namespace triaxis.CommandLine.ObjectOutput;

public interface IObjectDescriptor
{
    IReadOnlyList<IObjectField> Fields { get; }
}
