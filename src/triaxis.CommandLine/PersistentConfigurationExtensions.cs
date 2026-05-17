namespace triaxis.CommandLine;

using Microsoft.Extensions.Configuration;

/// <summary>
/// Scope-targeted, persistent updates to a scope-layered configuration.
/// </summary>
public static class PersistentConfigurationExtensions
{
    /// <summary>
    /// Mutates and persists the writable source registered for <paramref name="scope"/>.
    /// The mutation runs against that scope's
    /// <see cref="IPersistentConfigurationProvider"/>;
    /// <see cref="IPersistentConfigurationProvider.Save"/> is called once
    /// <paramref name="update"/> returns.
    /// </summary>
    /// <remarks>
    /// Scope targeting is deterministic — unlike position heuristics, it writes
    /// exactly the requested layer. It requires a scope-layered configuration source
    /// (<c>UseScopedConfiguration</c> / <c>UseDefaultConfiguration</c>); the source
    /// is located even when wrapped behind a host's chained configuration.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// No scope-layered configuration source is present, or <paramref name="scope"/>
    /// has no writable source.
    /// </exception>
    public static void Update(this IConfiguration configuration,
        ConfigurationScope scope,
        Action<IPersistentConfigurationProvider> update)
    {
        var lookup = FindScopedLookup(configuration)
            ?? throw new InvalidOperationException(
                "A scope-targeted configuration update requires a scope-layered " +
                "configuration source; none was found. Register one through " +
                "UseScopedConfiguration or UseDefaultConfiguration.");

        var provider = lookup.GetPersistentProvider(scope);
        update(provider);
        provider.Save();
    }

    private static IScopedPersistentLookup? FindScopedLookup(IConfiguration configuration)
    {
        if (configuration is not IConfigurationRoot root)
        {
            return null;
        }

        foreach (var provider in root.Providers)
        {
            switch (provider)
            {
                case IScopedPersistentLookup scoped:
                    return scoped;

                // A host (generic host / WebApplicationBuilder) wraps app
                // configuration behind a chained provider; descend into it.
                case ChainedConfigurationProvider chained
                    when FindScopedLookup(chained.Configuration) is { } nested:
                    return nested;
            }
        }

        return null;
    }
}
