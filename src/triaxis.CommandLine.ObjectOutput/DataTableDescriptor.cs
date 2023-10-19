namespace triaxis.CommandLine.ObjectOutput;

using System.Data;

class DataTableDescriptor : IObjectDescriptor
{
    private readonly DataTable _table;

    public DataTableDescriptor(DataTable table)
    {
        _table = table;
    }

    public IReadOnlyList<IObjectField> Fields => throw new NotImplementedException();
}
