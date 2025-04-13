namespace triaxis.CommandLine.ObjectOutput;

using System.Data;

class DataTableDescriptor : IObjectDescriptor
{
    public DataTableDescriptor(DataTable table)
    {
        Fields = table.Columns
            .OfType<DataColumn>().Select(c => {
                var t = typeof(DataColumnField<>).MakeGenericType(c.DataType);
                return (IObjectField)Activator.CreateInstance(t, c);
            })
            .ToArray();
    }

    public IReadOnlyList<IObjectField> Fields { get; }
}
