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
            return ((ToolHost)host).Invoke();
        }
        finally
        {
            host.StopAsync().GetAwaiter().GetResult();
        }
    }

    public static async Task<int> RunAsync(this IToolBuilder builder)
    {
        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            return await ((ToolHost)host).InvokeAsync();
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
