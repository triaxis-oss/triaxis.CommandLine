namespace triaxis.CommandLine.ObjectOutput;

using System.Data;

public class DefaultObjectDescriptorProvider<T> : IObjectDescriptorProvider<T>
{
    public IObjectDescriptor GetDescriptor(T? instance)
    {
        return instance is DataTable t ? new DataTableDescriptor(t) : SimpleObjectDescriptor<T>.Instance;
    }
}
