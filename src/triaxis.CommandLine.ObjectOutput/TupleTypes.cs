namespace triaxis.CommandLine.ObjectOutput;

/// <summary>
/// Recognises tuple types without depending on <c>ITuple</c>.
/// </summary>
/// <remarks>
/// <c>System.Runtime.CompilerServices.ITuple</c> only exists from netstandard2.1, so the
/// tuple descriptor used to be compiled out of the netstandard2.0 asset entirely — which
/// silently rendered every tuple as an empty object on .NET Framework. Matching on the
/// type name instead costs nothing and works on every target, including runtimes where
/// <c>ValueTuple</c> itself is absent.
/// </remarks>
static class TupleTypes
{
    public static bool IsTuple(Type type)
    {
        if (!type.IsGenericType)
        {
            return false;
        }

        var name = type.GetGenericTypeDefinition().FullName;

        return name is not null
            && (name.StartsWith("System.ValueTuple`", StringComparison.Ordinal)
                || name.StartsWith("System.Tuple`", StringComparison.Ordinal));
    }
}
