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

    public static object GetOrCreateValues(this MemberInfo[]? path, object target)
    {
        if (path is null)
        {
            return target;
        }

        foreach (var m in path)
        {
            var next = m.GetValue(target);
            if (next is null)
            {
                m.SetValue(target, next = Activator.CreateInstance(m.GetValueType()));
            }
            target = next;
        }

        return target;
    }

    public static object GetValue(this MemberInfo member, object target) =>
        member switch
        {
            FieldInfo fi => fi.GetValue(target),
            PropertyInfo pi => pi.GetValue(target, null),
            _ => throw new NotSupportedException(),
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

    public static MemberInfo GetRootMember(this IMemberBoundSymbol sym)
    {
        return sym.Path?.FirstOrDefault() ?? sym.Member;
    }
}
