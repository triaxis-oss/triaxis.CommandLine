namespace triaxis.CommandLine;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

public static partial class ToolBuilderExtensions
{
    /// <summary>
    /// Adds configuration sources to the builder, mirroring
    /// <see cref="IToolBuilder.ConfigureServices(Action{Microsoft.Extensions.DependencyInjection.IServiceCollection})"/>:
    /// the delegate runs immediately against the builder's
    /// <see cref="IToolBuilder.Configuration"/>, so sources are visible to any later
    /// configuration reads on the builder.
    /// </summary>
    /// <remarks>
    /// Composable — call it as many times as you want. Use the
    /// <see cref="ConfigureConfiguration(IToolBuilder, Action{HostBuilderContext, IConfigurationBuilder})"/>
    /// overload instead when the sources depend on the parsed command line
    /// (via <c>ctx.GetInvocationContext()</c>) or on other build-time state.
    /// </remarks>
    /// <returns>The same builder, for fluent chaining.</returns>
    public static IToolBuilder ConfigureConfiguration(this IToolBuilder builder, Action<IConfigurationBuilder> configure)
    {
        ((IHostBuilder)builder).ConfigureHostConfiguration(configure);
        return builder;
    }

    /// <summary>
    /// Adds configuration sources that are applied during
    /// <see cref="IHostBuilder.Build"/>, with access to the
    /// <see cref="HostBuilderContext"/> — including the build-time
    /// <see cref="InvocationContext"/> exposed via
    /// <c>ctx.GetInvocationContext()</c>, so sources can depend on the parsed
    /// command line.
    /// </summary>
    /// <remarks>
    /// Composable — call it as many times as you want. This is the configuration
    /// analogue of the deferred
    /// <c>IHostBuilder.ConfigureServices(Action&lt;HostBuilderContext, IServiceCollection&gt;)</c>
    /// overload, kept on <see cref="IToolBuilder"/> so the fluent chain is not
    /// broken by a cast to <see cref="IHostBuilder"/>.
    /// </remarks>
    /// <returns>The same builder, for fluent chaining.</returns>
    public static IToolBuilder ConfigureConfiguration(this IToolBuilder builder, Action<HostBuilderContext, IConfigurationBuilder> configure)
    {
        ((IHostBuilder)builder).ConfigureAppConfiguration(configure);
        return builder;
    }
}
