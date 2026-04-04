namespace triaxis.CommandLine;

using Microsoft.Extensions.Logging;

class DefaultCommandExecutor : ICommandExecutor
{
    private readonly IReadOnlyList<InvocationMiddleware> _middlewares;
    private readonly ILoggerFactory _loggerFactory;

    public DefaultCommandExecutor(IEnumerable<InvocationMiddleware> middlewares, ILoggerFactory loggerFactory)
    {
        _middlewares = middlewares.ToArray();
        _loggerFactory = loggerFactory;
    }

    public async Task ExecuteAsync(InvocationContext context, Func<Task> command)
    {
        try
        {
            Func<InvocationContext, Task> chain = _ => command();
            for (int i = _middlewares.Count - 1; i >= 0; i--)
            {
                var mw = _middlewares[i];
                var next = chain;
                chain = ctx => mw(ctx, next);
            }

            await chain(context);

            if (context.InvocationResult is not null)
            {
                await context.InvocationResult.EnsureCompleteAsync(context.GetCancellationToken());
            }
        }
        catch (CommandErrorException e)
        {
            context.ExitCode = -1;
            var logger = _loggerFactory.CreateLogger(context.CommandType.FullName ?? context.CommandType.Name);
            logger.LogError(e.Message, e.MessageArguments);
        }
    }
}
