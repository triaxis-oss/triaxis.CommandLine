namespace triaxis.CommandLine.ObjectOutput;

using System.ComponentModel;
using triaxis.Reflection;

class SimpleObjectDescriptorField<TValue> : IObjectField, IObjectField<TValue>
{

    public SimpleObjectDescriptorField(PropertyDescriptor pd, IPropertyGetter<TValue> accessor)
    {
        Name = pd.Name;
        Title = pd.DisplayName;
        Visibility = pd.IsBrowsable ? ObjectFieldVisibility.Standard : ObjectFieldVisibility.Extended;
        Converter = pd.Converter;
        Order = (pd.Attributes[typeof(ObjectOutputOrderAttribute)] as ObjectOutputOrderAttribute)?.Order ?? 0;
        Accessor = accessor;
    }

    public double Order { get; }

    public string Title { get; }

    public string Name { get; }

    public ObjectFieldVisibility Visibility { get; }

    public Type Type => typeof(TValue);

    public TypeConverter Converter { get; }

    public IPropertyGetter<TValue> Accessor { get; }

    IPropertyGetter IObjectField.Accessor => Accessor;
}
