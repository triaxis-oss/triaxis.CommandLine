namespace triaxis.CommandLine.ObjectOutput;

using System.Data;
using System.Runtime.CompilerServices;

public class DefaultObjectDescriptorProvider<T> : IObjectDescriptorProvider<T>
{
    public IObjectDescriptor GetDescriptor(T? instance)
    {
        return instance is DataTable t ? new DataTableDescriptor(t) :
            instance is ITuple ? TupleObjectDescriptor<T>.Instance :
            SimpleObjectDescriptor<T>.Instance;
    }
}
