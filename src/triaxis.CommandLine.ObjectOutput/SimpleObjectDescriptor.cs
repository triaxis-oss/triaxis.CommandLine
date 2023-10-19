
using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Reflection;
using Microsoft.Extensions.Options;
using triaxis.Reflection;

namespace triaxis.CommandLine.ObjectOutput;

sealed class SimpleObjectDescriptor<T> : IObjectDescriptor
{
    public static readonly IObjectDescriptor Instance = new SimpleObjectDescriptor<T>();

    private SimpleObjectDescriptor()
    {
        var pds = TypeDescriptor.GetProperties(typeof(T));
        var pis = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).ToLookup(pi => pi.Name);
        var fields = new IObjectField[pds.Count];
        Fields = fields;
        for (int i = 0; i < fields.Length; i++)
        {
            var pd = pds[i];
            var accessor = pis[pd.Name].FirstOrDefault(pi => pi.PropertyType == pd.PropertyType) is { } pi ?
                pi.GetGetter() :
                Activator.CreateInstance(typeof(PropertyDescriptorGetter<>).MakeGenericType(pd.PropertyType), pd);

            fields[i] = (IObjectField)Activator.CreateInstance(typeof(SimpleObjectDescriptorField<>).MakeGenericType(pd.PropertyType), pd, accessor);
        }
    }

    public IReadOnlyList<IObjectField> Fields { get; }

    class PropertyDescriptorGetter<TValue> : IPropertyGetter<TValue>
    {
        private readonly PropertyDescriptor _pd;

        public PropertyDescriptorGetter(PropertyDescriptor pd)
        {
            _pd = pd;
        }

        public PropertyInfo Property => null!;

        public TValue Get(object target)
        {
            return (TValue)_pd.GetValue(target);
        }

        object IPropertyGetter.Get(object target)
        {
            return _pd.GetValue(target);
        }
    }
}
