namespace triaxis.CommandLine.ObjectOutput;

using System.Reflection;
using triaxis.Reflection;

/// <summary>
/// Reads a field or property straight through <see cref="MemberInfo"/>.
/// </summary>
/// <remarks>
/// Used instead of <c>GetGetter()</c> for tuple elements. <c>GetGetter()</c> recurses
/// into itself when handed a <see cref="FieldInfo"/> on the netstandard2.0 asset of
/// triaxis.Reflection.PropertyAccess 1.3.0, which overflows the stack — and tuple
/// elements are fields, so they were the only caller that hit it. Reading reflectively
/// is slower than an emitted accessor, but this is the fallback path that generated
/// descriptors replace, and it drops an emit dependency the trimmer wants gone anyway.
/// </remarks>
sealed class MemberValueGetter : IPropertyGetter
{
    private readonly FieldInfo? _field;
    private readonly PropertyInfo? _property;

    public MemberValueGetter(FieldInfo field) => _field = field;
    public MemberValueGetter(PropertyInfo property) => _property = property;

    public PropertyInfo Property
        => _property ?? throw new NotSupportedException("This accessor reads a field, not a property.");

    public object Get(object target)
        => (_field is not null ? _field.GetValue(target) : _property!.GetValue(target))!;
}
