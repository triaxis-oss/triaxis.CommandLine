namespace triaxis.CommandLine;

using Microsoft.Extensions.Hosting;

public static class ToolBuilderRunExtensions
{
    public static int Run(this IToolBuilder builder)
    {
        using var host = builder.Build();
        host.Start();
        try
        {
            return host switch
            {
                ToolHost th => th.Invoke(),
                StandaloneHost sh => sh.Invoke(),
                _ => throw new InvalidOperationException($"Unsupported host type '{host.GetType()}'."),
            };
        }
        finally
        {
            host.StopAsync().GetAwaiter().GetResult();
        }
    }

    public static async Task<int> RunAsync(this IToolBuilder builder)
    {
        var host = builder.Build();
        await using var hostDisposal = host.AsAsyncDisposable();
        await host.StartAsync();
        try
        {
            return host switch
            {
                ToolHost th => await th.InvokeAsync(),
                StandaloneHost sh => await sh.InvokeAsync(),
                _ => throw new InvalidOperationException($"Unsupported host type '{host.GetType()}'."),
            };
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
