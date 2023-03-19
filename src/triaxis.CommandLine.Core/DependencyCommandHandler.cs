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

        try
        {
            context.InvocationResult = await host.Services.GetRequiredService<ICommandExecutor>().ExecuteCommandAsync(CommandType);
            return 0;
        }
        catch (Exception e)
        {
            var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(CommandType);
            if (e is CommandErrorException ce)
            {
                logger.LogError(ce.Message, ce.MessageArguments);
            }
            else
            {
                logger.LogError(e, "Error while executing command");
            }
            return -1;
        }
    }
}
