namespace triaxis.CommandLine.ObjectOutput;

public interface IObjectDescriptorProvider<T>
{
    IObjectDescriptor GetDescriptor(T? instance);
}
