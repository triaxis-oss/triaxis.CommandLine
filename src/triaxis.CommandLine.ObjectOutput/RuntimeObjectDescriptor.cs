namespace triaxis.CommandLine.ObjectOutput;

using System.Collections.Concurrent;
using System.Reflection;

/// <summary>
/// Resolves the descriptor for a type only known at run time, so formatters can descend
/// into nested values the same way they describe the root object.
/// </summary>
static class RuntimeObjectDescriptor
{
    private static readonly ConcurrentDictionary<Type, IObjectDescriptor> s_cache = new();

    public static IObjectDescriptor For(Type type)
    {
        if (ObjectDescriptorRegistry.TryGet(type) is { } generated)
        {
            return generated;
        }

        if (!ObjectOutputFeatures.ReflectionFallbackEnabled)
        {
            throw new NotSupportedException(
                $"No generated object descriptor for '{type}', and the reflective fallback is disabled. " +
                "Return the type from a command so the source generator describes it, or re-enable " +
                "<EnableObjectOutputReflectionFallback>.");
        }

        return Reflect(type);
    }

    // Deliberately not [RequiresUnreferencedCode]: the netstandard targets cannot reach a
    // public copy of the attribute, and the trim warning already surfaces at the
    // TypeDescriptor call inside SimpleObjectDescriptor. Removal is driven by the feature
    // switch above, which does not depend on the annotation.
    private static IObjectDescriptor Reflect(Type type)
        => s_cache.GetOrAdd(type, static t =>
            (IObjectDescriptor)typeof(SimpleObjectDescriptor<>)
                .MakeGenericType(t)
                .GetField(nameof(SimpleObjectDescriptor<object>.Instance), BindingFlags.Public | BindingFlags.Static)!
                .GetValue(null)!);
}
