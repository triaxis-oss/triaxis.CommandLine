namespace triaxis.CommandLine.ObjectOutput;

using triaxis.Reflection;

public interface IObjectField
{
    string Title { get; }
    string Name { get; }
    ObjectFieldVisibility Visibility { get; }
    Type Type { get; }

    /// <summary>
    /// Format string applied to <see cref="IFormattable"/> values, from
    /// <see cref="ObjectOutputAttribute.Format"/>. <see langword="null"/> selects the
    /// type's general format.
    /// </summary>
    /// <remarks>
    /// A plain string rather than a <c>TypeConverter</c> so a descriptor can be produced
    /// at compile time — resolving a converter requires <c>TypeDescriptor</c>, which is
    /// annotated <c>RequiresUnreferencedCode</c> and cannot be made trim-safe.
    /// </remarks>
    string? Format { get; }

    IPropertyGetter Accessor { get; }
}

public interface IObjectField<T> : IObjectField
{
    new IPropertyGetter<T> Accessor { get; }
}
