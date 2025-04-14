namespace triaxis.CommandLine.ObjectOutput;

using System.Data;

class DataTableDescriptor : IObjectDescriptor
{
    public DataTableDescriptor(DataTable table)
    {
        Fields = table.Columns
            .OfType<DataColumn>().Select(c => {
                var type = c.DataType;
                if (c.AllowDBNull && type.IsValueType)
                {
                    type = typeof(Nullable<>).MakeGenericType(type);
                }
                var t = typeof(DataColumnField<>).MakeGenericType(type);
                return (IObjectField)Activator.CreateInstance(t, c);
            })
            .ToArray();
    }

    public IReadOnlyList<IObjectField> Fields { get; }
}
