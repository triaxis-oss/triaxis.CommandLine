namespace triaxis.CommandLine;

using System.CommandLine;
using System.CommandLine.Hosting;
using System.CommandLine.Invocation;
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
        var optOutput = new Option<ObjectOutputFormat>(new[] { "-o", "--output" }, () => defaultOutputFormat?.Invoke() ?? ObjectOutputFormat.Table, "Output format");

        builder.RootCommand.AddGlobalOption(optOutput);

        builder.AddMiddleware(ObjectOutputMiddleware);
        builder.ConfigureServices((context, services) =>
        {
            services.TryAddTransient(typeof(IObjectOutputHandler<>), typeof(DefaultObjectOutputHandler<>));
            services.TryAddTransient<IObjectOutputHandler, DynamicObjectOutputHandler>();
            services.TryAddSingleton(typeof(IObjectDescriptorProvider<>), typeof(DefaultObjectDescriptorProvider<>));

            services.TryAddTransient<TableObjectFormatterProvider>();
            services.TryAddTransient<YamlObjectFormatterProvider>();
            services.TryAddTransient<JsonObjectFormatterProvider>();
            services.TryAddTransient<RawObjectFormatterProvider>();

            services.TryAddTransient<IObjectFormatterProvider>(sp =>
            {
                var fmt = builder.GetInvocationContext().ParseResult.GetValueForOption(optOutput);
                return fmt switch
                {
                    ObjectOutputFormat.Yaml => sp.GetRequiredService<YamlObjectFormatterProvider>(),
                    ObjectOutputFormat.Json => sp.GetRequiredService<JsonObjectFormatterProvider>(),
                    ObjectOutputFormat.Raw => sp.GetRequiredService<RawObjectFormatterProvider>(),
                    _ => sp.GetRequiredService<TableObjectFormatterProvider>(),
                };
            });

            services.Configure<TableOutputOptions>(config =>
            {
                var fmt = builder.GetInvocationContext().ParseResult.GetValueForOption(optOutput);
                if (fmt == ObjectOutputFormat.Wide)
                {
                    config.Wide = true;
                }
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
            if (context.GetHost().Services.GetService(typeof(IObjectOutputHandler<>).MakeGenericType(objectType)) is IObjectOutputHandler handler)
            {
                await handler.ProcessOutputAsync(cir, context.GetCancellationToken());
            }
        }
    }
}
