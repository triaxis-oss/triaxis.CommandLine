namespace triaxis.CommandLine;

using System.Reflection;

static class MemberInfoExtensions
{
    public static Type GetValueType(this MemberInfo member) =>
        member switch
        {
            FieldInfo fi => fi.FieldType,
            PropertyInfo pi => pi.PropertyType,
            _ => throw new NotSupportedException()
        };

    public static void SetValue(this MemberInfo member, object target, object? value)
    {
        switch (member)
        {
            case FieldInfo fi: fi.SetValue(target, value); break;
            case PropertyInfo pi: pi.SetValue(target, value); break;
            default: throw new NotSupportedException();
        };
    }
}
