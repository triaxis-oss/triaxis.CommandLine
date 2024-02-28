namespace triaxis.CommandLine.ObjectOutput;

using System.ComponentModel;
using triaxis.Reflection;

class SimpleObjectDescriptorField<TValue> : IObjectField, IObjectField<TValue>, IObjectFieldOrdering
{

    public SimpleObjectDescriptorField(PropertyDescriptor pd, IPropertyGetter<TValue> accessor)
    {
        var attr = pd.Attributes[typeof(ObjectOutputAttribute)] as ObjectOutputAttribute;

        Name = pd.Name;
        Title = pd.DisplayName;
        Before = attr?.Before;
        After = attr?.After;
        Visibility = attr?.Visibility ?? (pd.IsBrowsable ? ObjectFieldVisibility.Standard : ObjectFieldVisibility.Extended);
        Converter = pd.Converter;
        Accessor = accessor;
    }

    public string? Before { get; }
    public string? After { get; }

    public string Title { get; }

    public string Name { get; }

    public ObjectFieldVisibility Visibility { get; }

    public Type Type => typeof(TValue);

    public TypeConverter Converter { get; }

    public IPropertyGetter<TValue> Accessor { get; }

    IPropertyGetter IObjectField.Accessor => Accessor;
}
