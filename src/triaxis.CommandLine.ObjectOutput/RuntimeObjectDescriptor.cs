namespace triaxis.CommandLine.ObjectOutput;

using System.Collections.Concurrent;
using System.Reflection;

/// <summary>
/// Resolves the descriptor for a type only known at run time, so formatters can
/// descend into nested values the same way they describe the root object.
/// </summary>
/// <remarks>
/// This is the reflective stand-in for descriptors the source generator will emit:
/// the nested walk needs a descriptor per encountered type, and until generation
/// covers them the shape has to be discovered at run time.
/// </remarks>
static class RuntimeObjectDescriptor
{
    private static readonly ConcurrentDictionary<Type, IObjectDescriptor> s_cache = new();

    public static IObjectDescriptor For(Type type)
        => s_cache.GetOrAdd(type, static t =>
            (IObjectDescriptor)typeof(SimpleObjectDescriptor<>)
                .MakeGenericType(t)
                .GetField(nameof(SimpleObjectDescriptor<object>.Instance), BindingFlags.Public | BindingFlags.Static)!
                .GetValue(null)!);
}
