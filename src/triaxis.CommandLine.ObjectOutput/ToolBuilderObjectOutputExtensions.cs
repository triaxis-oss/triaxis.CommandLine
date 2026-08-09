namespace triaxis.CommandLine;

using System.CommandLine;
using System.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection.Extensions;
using triaxis.CommandLine.ObjectOutput;
using triaxis.CommandLine.ObjectOutput.Formatters;

public static class ToolBuilderObjectOutputExtensions
{
    public static readonly Option<ObjectOutputFormat> OutputFormatOption =
        new("--output", "-o") { DefaultValueFactory = _ => ObjectOutputFormat.Table, Description = "Output format", Recursive = true };

    public static IToolBuilder UseObjectOutput(this IToolBuilder builder)
    {
        var optOutput = OutputFormatOption;

        // Insert ahead of the System.CommandLine defaults (--help / --version) so
        // help output lists --output alongside the other user-configured recursive
        // options, ahead of the builtins, on every inheriting subcommand.
        builder.AddRecursiveOption(optOutput);

        builder.AddMiddleware(ObjectOutputMiddleware);
        builder.ConfigureServices(services =>
        {
            services.TryAddSingleton<IOutputStreamProvider>(new ConsoleOutputStreamProvider());

            // Closed registrations first: they are what makes value-type and tuple output
            // work under NativeAOT, and TryAdd below then leaves them in place.
            ObjectOutputHandlerRegistry.ApplyRegistrations(services);

            services.TryAddTransient<IObjectOutputHandler, DynamicObjectOutputHandler>();

            // The open-generic registrations and the DataTable handler all resolve shape
            // at run time, so they sit behind the switch with the rest of it. Leaving them
            // rooted keeps MakeGenericType — and DataTableDescriptor's own — reachable,
            // which is IL3050 even in an app whose every output type is generated.
            if (ObjectOutputFeatures.ReflectionFallbackEnabled)
            {
                services.TryAddTransient(typeof(IObjectOutputHandler<>), typeof(DefaultObjectOutputHandler<>));
                services.TryAddTransient(typeof(IObjectOutputHandler<DataTable>), typeof(DataTableObjectOutputHandler));
                services.TryAddSingleton(typeof(IObjectDescriptorProvider<>), typeof(DefaultObjectDescriptorProvider<>));
            }

            services.TryAddTransient<TableObjectFormatterProvider>();
            services.TryAddTransient<YamlObjectFormatterProvider>();
            services.TryAddTransient<JsonObjectFormatterProvider>();
            services.TryAddTransient<RawObjectFormatterProvider>();
            services.TryAddTransient<DiscardObjectFormatterProvider>();

            services.TryAddTransient<IObjectFormatterProvider>(sp =>
            {
                var fmt = sp.GetRequiredService<ParseResult>().GetValue(optOutput);
                return fmt switch
                {
                    ObjectOutputFormat.Yaml => sp.GetRequiredService<YamlObjectFormatterProvider>(),
                    ObjectOutputFormat.Json => sp.GetRequiredService<JsonObjectFormatterProvider>(),
                    ObjectOutputFormat.Raw => sp.GetRequiredService<RawObjectFormatterProvider>(),
                    ObjectOutputFormat.None => sp.GetRequiredService<DiscardObjectFormatterProvider>(),
                    _ => sp.GetRequiredService<TableObjectFormatterProvider>(),
                };
            });

            services.AddTransient<IConfigureOptions<TableOutputOptions>>(sp =>
            {
                var fmt = sp.GetRequiredService<ParseResult>().GetValue(optOutput);
                return new ConfigureOptions<TableOutputOptions>(config =>
                {
                    if (fmt == ObjectOutputFormat.Wide)
                    {
                        config.Wide = true;
                    }
                });
            });
        });
        return builder;
    }

    /// <summary>
    /// Finds the handler for an element type known only at run time.
    /// </summary>
    /// <remarks>
    /// The generated lookup closes the generic at compile time. The
    /// <c>MakeGenericType</c> path behind it cannot be used under NativeAOT for a value
    /// type, and is annotated <c>RequiresDynamicCode</c>, so it sits behind the same
    /// feature switch as the rest of the reflective machinery — with the switch off it is
    /// eliminated and the IL3050 warning goes with it.
    /// </remarks>
    internal static IObjectOutputHandler? ResolveHandler(IServiceProvider services, Type elementType)
    {
        if (ObjectOutputHandlerRegistry.TryGet(elementType, services) is { } generated)
        {
            return generated;
        }

        if (!ObjectOutputFeatures.ReflectionFallbackEnabled)
        {
            return null;
        }

        return services.GetService(typeof(IObjectOutputHandler<>).MakeGenericType(elementType)) as IObjectOutputHandler;
    }

    private static async Task ObjectOutputMiddleware(InvocationContext context, Func<InvocationContext, Task> next)
    {
        await next(context);

        if (context.InvocationResult is ICommandInvocationResult cir &&
            cir.GetType().GetInterfaces().FirstOrDefault(intf => intf.IsGenericType && intf.GetGenericTypeDefinition() == typeof(ICommandInvocationResult<>)) is {} tcir)
        {
            var objectType = tcir.GetGenericArguments()[0];
            var handler = ResolveHandler(context.Services, objectType)
                ?? throw new NotSupportedException(
                    $"No object output handler for '{objectType}'. Return the type from a command so the " +
                    "source generator emits one, or re-enable <EnableObjectOutputReflectionFallback>.");

            await handler.ProcessOutputAsync(cir, context.GetCancellationToken());
        }
    }
}
