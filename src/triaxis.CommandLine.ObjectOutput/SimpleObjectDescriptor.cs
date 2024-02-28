
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
        var pis = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).ToLookup(pi => pi.Name);
        var pds = TypeDescriptor.GetProperties(typeof(T));

        Fields = pds.Cast<PropertyDescriptor>()
            .Select(pd => (pd, pi: pis[pd.Name].FirstOrDefault(pi => pi.PropertyType == pd.PropertyType)))
            .OrderBy(pair => pair.pi?.MetadataToken ?? int.MaxValue)    // declaration order first, custom properties last
            .ThenBy(pair => pair.pd.Name, StringComparer.OrdinalIgnoreCase)
            .Select(pair =>
            {
                var (pd, pi) = pair;
                var accessor = pi?.GetGetter() ?? Activator.CreateInstance(typeof(PropertyDescriptorGetter<>).MakeGenericType(pd.PropertyType), pd);
                return (IObjectField)Activator.CreateInstance(typeof(SimpleObjectDescriptorField<>).MakeGenericType(pd.PropertyType), pd, accessor);
            })
            .Ordered();
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
