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
            services.TryAddTransient(typeof(IObjectOutputHandler<>), typeof(DefaultObjectOutputHandler<>));
            services.TryAddTransient(typeof(IObjectOutputHandler<DataTable>), typeof(DataTableObjectOutputHandler));
            services.TryAddTransient<IObjectOutputHandler, DynamicObjectOutputHandler>();
            services.TryAddSingleton(typeof(IObjectDescriptorProvider<>), typeof(DefaultObjectDescriptorProvider<>));

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

    private static async Task ObjectOutputMiddleware(InvocationContext context, Func<InvocationContext, Task> next)
    {
        await next(context);

        if (context.InvocationResult is ICommandInvocationResult cir &&
            cir.GetType().GetInterfaces().FirstOrDefault(intf => intf.IsGenericType && intf.GetGenericTypeDefinition() == typeof(ICommandInvocationResult<>)) is {} tcir)
        {
            var objectType = tcir.GetGenericArguments()[0];
            if (context.Services.GetService(typeof(IObjectOutputHandler<>).MakeGenericType(objectType)) is IObjectOutputHandler handler)
            {
                await handler.ProcessOutputAsync(cir, context.GetCancellationToken());
            }
        }
    }
}
