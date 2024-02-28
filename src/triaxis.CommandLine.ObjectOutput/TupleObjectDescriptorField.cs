namespace triaxis.CommandLine.ObjectOutput;

using System.ComponentModel;
using System.Reflection;
using triaxis.Reflection;

class TupleObjectDescriptorField<TValue> : IObjectField, IObjectField<TValue>, IObjectFieldOrdering, IPropertyGetter<TValue>
{
    private readonly IPropertyGetter _getter;
    private readonly IObjectField<TValue> _field;

    public TupleObjectDescriptorField(IPropertyGetter tupleExtractor, IObjectField<TValue> field)
    {
        _getter = tupleExtractor;
        _field = field;
    }

    public string Title => _field.Title;
    public string Name => _field.Name;
    public ObjectFieldVisibility Visibility => _field.Visibility;
    public Type Type => _field.Type;
    public TypeConverter Converter => _field.Converter;
    public IPropertyGetter<TValue> Accessor => this;
    IPropertyGetter IObjectField.Accessor => this;

    public string? Before => (_field as IObjectFieldOrdering)?.Before;
    public string? After => (_field as IObjectFieldOrdering)?.After;

    public TValue Get(object target) => _field.Accessor.Get(_getter.Get(target));
    object? IPropertyGetter.Get(object target) => Get(target);
    PropertyInfo IPropertyAccessor.Property => _field.Accessor.Property;
}
