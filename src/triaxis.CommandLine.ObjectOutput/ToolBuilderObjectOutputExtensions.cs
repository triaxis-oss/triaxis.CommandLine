namespace triaxis.CommandLine;

using System.CommandLine;
using System.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using triaxis.CommandLine.ObjectOutput;
using triaxis.CommandLine.ObjectOutput.Formatters;

public static class ToolBuilderObjectOutputExtensions
{
    public static IToolBuilder UseObjectOutput(this IToolBuilder builder)
    {
        Func<ObjectOutputFormat>? defaultOutputFormat = null;
        var optOutput = new Option<ObjectOutputFormat>("--output", "-o") { DefaultValueFactory = _ => defaultOutputFormat?.Invoke() ?? ObjectOutputFormat.Table, Description = "Output format" };

        builder.RootCommand.Options.Add(optOutput);

        builder.AddResultProcessor(ObjectOutputProcessor);
        builder.ConfigureServices((context, services) =>
        {
            services.TryAddSingleton<TextWriter>(Console.Out);
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
                var fmt = builder.GetParseResult().GetValue(optOutput);
                return fmt switch
                {
                    ObjectOutputFormat.Yaml => sp.GetRequiredService<YamlObjectFormatterProvider>(),
                    ObjectOutputFormat.Json => sp.GetRequiredService<JsonObjectFormatterProvider>(),
                    ObjectOutputFormat.Raw => sp.GetRequiredService<RawObjectFormatterProvider>(),
                    ObjectOutputFormat.None => sp.GetRequiredService<DiscardObjectFormatterProvider>(),
                    _ => sp.GetRequiredService<TableObjectFormatterProvider>(),
                };
            });

            services.Configure<TableOutputOptions>(config =>
            {
                var fmt = builder.GetParseResult().GetValue(optOutput);
                if (fmt == ObjectOutputFormat.Wide)
                {
                    config.Wide = true;
                }
            });
        });
        return builder;
    }

    private static async Task ObjectOutputProcessor(IServiceProvider services, ParseResult parseResult, ICommandInvocationResult? result, CancellationToken cancellationToken)
    {
        if (result is ICommandInvocationResult cir &&
            cir.GetType().GetInterfaces().FirstOrDefault(intf => intf.IsGenericType && intf.GetGenericTypeDefinition() == typeof(ICommandInvocationResult<>)) is {} tcir)
        {
            var objectType = tcir.GetGenericArguments()[0];
            if (services.GetService(typeof(IObjectOutputHandler<>).MakeGenericType(objectType)) is IObjectOutputHandler handler)
            {
                await handler.ProcessOutputAsync(cir, cancellationToken);
            }
        }
    }
}
