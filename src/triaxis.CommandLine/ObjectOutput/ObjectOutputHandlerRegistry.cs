namespace triaxis.CommandLine.ObjectOutput;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Holds the closed <see cref="IObjectOutputHandler{T}"/> registrations and dispatch
/// contributed by source-generated code.
/// </summary>
/// <remarks>
/// Resolving a handler used to mean <c>MakeGenericType</c> over the element type against
/// an open-generic DI registration. NativeAOT cannot synthesise a generic instantiation
/// over a value type that was never statically rooted, so every struct output type — and
/// therefore every tuple, since <c>ValueTuple</c> is a struct — failed at run time with
/// "Unable to create a generic service ... because 'T' is a ValueType". Closing the
/// generic at generation time roots the instantiation instead.
/// </remarks>
public static class ObjectOutputHandlerRegistry
{
    private static Action<IServiceCollection>[] s_registrations = [];
    private static Func<Type, IServiceProvider, IObjectOutputHandler?>[] s_lookups = [];

    /// <summary>
    /// Adds one assembly's generated handler registrations and dispatch.
    /// </summary>
    /// <param name="configureServices">Registers the closed handler services.</param>
    /// <param name="lookup">
    /// Resolves a handler for an element type, or <see langword="null"/> to defer to the
    /// remaining lookups.
    /// </param>
    public static void Register(Action<IServiceCollection> configureServices,
        Func<Type, IServiceProvider, IObjectOutputHandler?> lookup)
    {
        if (configureServices is null)
        {
            throw new ArgumentNullException(nameof(configureServices));
        }
        if (lookup is null)
        {
            throw new ArgumentNullException(nameof(lookup));
        }

        // module initializers can run concurrently across assemblies
        Append(ref s_registrations, configureServices);
        Append(ref s_lookups, lookup);
    }

    public static void ApplyRegistrations(IServiceCollection services)
    {
        foreach (var register in s_registrations)
        {
            register(services);
        }
    }

    public static IObjectOutputHandler? TryGet(Type elementType, IServiceProvider services)
    {
        foreach (var lookup in s_lookups)
        {
            if (lookup(elementType, services) is { } handler)
            {
                return handler;
            }
        }

        return null;
    }

    private static void Append<T>(ref T[] target, T value)
    {
        T[] current, updated;
        do
        {
            current = target;
            updated = new T[current.Length + 1];
            Array.Copy(current, updated, current.Length);
            updated[current.Length] = value;
        }
        while (Interlocked.CompareExchange(ref target, updated, current) != current);
    }
}
