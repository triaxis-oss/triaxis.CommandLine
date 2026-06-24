namespace triaxis.CommandLine;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

public static partial class ToolBuilderExtensions
{
    // Opaque, identity-keyed slot for the shared builder: this is private state, not a
    // discoverable contract on the host's Properties bag.
    private static readonly object ScopedBuilderKey = new();

    /// <summary>
    /// Adds a scope-layered configuration source. Sources are grouped by
    /// <see cref="ConfigurationScope"/> and optionally subtree-remapped via
    /// <see cref="ScopedConfigurationBuilder"/>; the resulting single source resolves
    /// every key so that a more specific scope always wins, and a remapped subtree
    /// never overrides a more specific scope's primary value.
    /// </summary>
    /// <remarks>
    /// Calling this repeatedly on the same host accumulates onto one shared
    /// <see cref="ScopedConfigurationBuilder"/> and emits a <b>single</b> source, rather
    /// than stacking a new layer per call. This keeps the scope precedence and
    /// scope-targeted <see cref="PersistentConfigurationExtensions.Update">Update</see>
    /// invariants intact — both only hold within one source — so a later call's less
    /// specific scope cannot clobber an earlier call's more specific scope.
    /// The source is built at <see cref="IHostBuilder.Build"/> (like
    /// <c>UseDefaultConfiguration</c>), so additions from every call are folded in and
    /// file presence is evaluated at run time.
    /// </remarks>
    public static IHostBuilder UseScopedConfiguration(this IHostBuilder builder, Action<ScopedConfigurationBuilder> configure)
    {
        if (builder.Properties.TryGetValue(ScopedBuilderKey, out var existing))
        {
            configure((ScopedConfigurationBuilder)existing);
            return builder;
        }

        var scoped = new ScopedConfigurationBuilder();
        builder.Properties[ScopedBuilderKey] = scoped;
        configure(scoped);
        // Deferred so additions from later calls are folded into the one source.
        builder.ConfigureAppConfiguration((_, cfg) => cfg.Add(scoped.BuildSource()));
        return builder;
    }

    /// <inheritdoc cref="UseScopedConfiguration(IHostBuilder, Action{ScopedConfigurationBuilder})"/>
    /// <returns>The same builder, for fluent chaining.</returns>
    public static IToolBuilder UseScopedConfiguration(this IToolBuilder builder, Action<ScopedConfigurationBuilder> configure)
    {
        ((IHostBuilder)builder).UseScopedConfiguration(configure);
        return builder;
    }
}
