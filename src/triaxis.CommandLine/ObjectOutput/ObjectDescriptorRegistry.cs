namespace triaxis.CommandLine.ObjectOutput;

/// <summary>
/// Holds the descriptor lookups contributed by source-generated code.
/// </summary>
/// <remarks>
/// Generated descriptors live in the consuming assembly, which this package cannot
/// reference, so registration is inverted: the generator emits a module initializer that
/// calls <see cref="Register"/> before any command runs. Several assemblies may each
/// contribute a lookup, so registrations accumulate rather than replace.
/// </remarks>
public static class ObjectDescriptorRegistry
{
    private static Func<Type, IObjectDescriptor?>[] s_lookups = [];

    /// <summary>
    /// Adds a lookup consulted before the reflective fallback. Returning
    /// <see langword="null"/> defers to the remaining lookups.
    /// </summary>
    public static void Register(Func<Type, IObjectDescriptor?> lookup)
    {
        if (lookup is null)
        {
            throw new ArgumentNullException(nameof(lookup));
        }

        // module initializers can run concurrently across assemblies
        Func<Type, IObjectDescriptor?>[] current, updated;
        do
        {
            current = s_lookups;
            updated = new Func<Type, IObjectDescriptor?>[current.Length + 1];
            Array.Copy(current, updated, current.Length);
            updated[current.Length] = lookup;
        }
        while (Interlocked.CompareExchange(ref s_lookups, updated, current) != current);
    }

    public static IObjectDescriptor? TryGet(Type type)
    {
        foreach (var lookup in s_lookups)
        {
            if (lookup(type) is { } descriptor)
            {
                return descriptor;
            }
        }

        return null;
    }
}
