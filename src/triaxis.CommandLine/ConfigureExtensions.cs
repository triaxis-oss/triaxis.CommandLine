namespace triaxis.CommandLine;

public static partial class ToolBuilderExtensions
{
    /// <summary>
    /// Runs an arbitrary builder-customization callback as part of the fluent chain
    /// and returns the same builder. This is the runtime counterpart of the
    /// <see cref="ConfigureAttribute">[Configure]</see> hook: the callback can call
    /// <c>builder.ConfigureServices(…)</c>, any
    /// <see cref="Microsoft.Extensions.Hosting.IHostBuilder"/> extension, or the
    /// <c>UseDefaultLogging</c>/<c>UseDefaultConfiguration</c> helpers to compose
    /// builder setup.
    /// </summary>
    /// <returns>The same builder, for fluent chaining.</returns>
    public static IToolBuilder Configure(this IToolBuilder builder, Action<IToolBuilder> configure)
    {
        configure(builder);
        return builder;
    }
}
