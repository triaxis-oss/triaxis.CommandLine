using System.Text;

namespace triaxis.CommandLine;

static class TypeExtensions
{
    public static Type? GetIEnumerableElementType(this Type t)
    {
        static Type? ExtractType(Type t)
        {
            return t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>)?
                t.GetGenericArguments()[0] :
                null;
        }

        var et = ExtractType(t) ?? t.GetInterfaces().Select(ExtractType).SingleOrDefault(t => t is not null);

        // we don't want to enumerate bytes or characters
        return et == typeof(byte) || et == typeof(char) ? null : et;
    }
}
