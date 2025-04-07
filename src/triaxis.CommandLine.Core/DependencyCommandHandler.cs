namespace triaxis.CommandLine;

using System.CommandLine.Hosting;
using System.CommandLine.Invocation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal class DependencyCommandHandler : ICommandHandler
{
    public Type CommandType { get; }
    public CommandAttribute Attribute { get; }

    public DependencyCommandHandler(Type commandType, CommandAttribute attribute)
    {
        CommandType = commandType;
        Attribute = attribute;
    }

    public int Invoke(InvocationContext context)
        => InvokeAsync(context).GetAwaiter().GetResult();

    public async Task<int> InvokeAsync(InvocationContext context)
    {
        var host = context.GetHost();
        context.InvocationResult = await host.Services.GetRequiredService<ICommandExecutor>().ExecuteCommandAsync(CommandType);
        return 0;
    }
}
