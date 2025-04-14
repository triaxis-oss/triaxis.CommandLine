using System.ComponentModel;
using System.Data;
using System.Reflection;
using triaxis.Reflection;

namespace triaxis.CommandLine.ObjectOutput;

class DataColumnField<TValue> : IObjectField, IObjectField<TValue>, IPropertyGetter<DataRow, TValue>
{
    public DataColumnField(DataColumn column)
    {
        Column = column;
    }

    public DataColumn Column { get; }

    public string Title => Column.Caption;
    public string Name => Column.ColumnName;

    public ObjectFieldVisibility Visibility => ObjectFieldVisibility.Standard;
    public Type Type => typeof(TValue);
    public TypeConverter Converter => TypeDescriptor.GetConverter(Type);
    public IPropertyGetter Accessor => this;

    public PropertyInfo Property => throw new NotImplementedException();

    IPropertyGetter<TValue> IObjectField<TValue>.Accessor => this;

    public TValue Get(DataRow target) => target[Column] is TValue val ? val : default!;
    public TValue Get(object target) => Get((DataRow)target);
    object? IPropertyGetter.Get(object target) => Get(target);
}
