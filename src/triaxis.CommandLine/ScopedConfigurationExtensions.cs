namespace triaxis.CommandLine;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

public static partial class ToolBuilderExtensions
{
    /// <summary>
    /// Adds a scope-layered configuration source. Sources are grouped by
    /// <see cref="ConfigurationScope"/> and optionally subtree-remapped via
    /// <see cref="ScopedConfigurationBuilder"/>; the resulting single source resolves
    /// every key so that a more specific scope always wins, and a remapped subtree
    /// never overrides a more specific scope's primary value.
    /// </summary>
    /// <remarks>
    /// Deferred until <see cref="IHostBuilder.Build"/> (like
    /// <c>UseDefaultConfiguration</c>), so file presence is evaluated at run time.
    /// </remarks>
    public static IHostBuilder UseScopedConfiguration(this IHostBuilder builder, Action<ScopedConfigurationBuilder> configure)
    {
        var scoped = new ScopedConfigurationBuilder();
        configure(scoped);
        var source = scoped.BuildSource();
        builder.ConfigureAppConfiguration((_, cfg) => cfg.Add(source));
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
