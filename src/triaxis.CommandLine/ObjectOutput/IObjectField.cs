namespace triaxis.CommandLine.ObjectOutput;

using System.ComponentModel;
using triaxis.Reflection;

public interface IObjectField
{
    string Title { get; }
    string Name { get; }
    ObjectFieldVisibility Visibility { get; }
    Type Type { get; }
    TypeConverter Converter { get; }

    IPropertyGetter Accessor { get; }
}

public interface IObjectField<T> : IObjectField
{
    new IPropertyGetter<T> Accessor { get; }
}
