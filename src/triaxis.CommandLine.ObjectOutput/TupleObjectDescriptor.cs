namespace triaxis.CommandLine.ObjectOutput;

using System.Collections.Generic;
using System.Reflection;
using triaxis.Reflection;

sealed class TupleObjectDescriptor<T> : IObjectDescriptor
{
    public static readonly TupleObjectDescriptor<T> Instance = new();

    public IReadOnlyList<IObjectField> Fields { get; }

    public TupleObjectDescriptor()
    {
        Fields = Enumerable.Concat(
            typeof(T).GetProperties().Select(pi => (pi.PropertyType, (IPropertyGetter)new MemberValueGetter(pi))),
            typeof(T).GetFields().Select(fi => (fi.FieldType, (IPropertyGetter)new MemberValueGetter(fi))))
            .Select(pair =>
            {
                var (t, getter) = pair;
                var subType = TupleTypes.IsTuple(t) ? typeof(TupleObjectDescriptor<>) : typeof(SimpleObjectDescriptor<>);
                var subDescriptor = (IObjectDescriptor)subType.MakeGenericType(t).InvokeMember("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.GetField, null, null, null);
                return (getter, subDescriptor);
            })
            .SelectMany(pair => pair.subDescriptor.Fields.Select(
                field => (IObjectField)Activator.CreateInstance(typeof(TupleObjectDescriptorField<>).MakeGenericType(field.Type), pair.getter, field)
            )).Ordered();
    }
}
