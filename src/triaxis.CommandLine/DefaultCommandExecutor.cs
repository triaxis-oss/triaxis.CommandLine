namespace triaxis.CommandLine;

using Microsoft.Extensions.Logging;

class DefaultCommandExecutor : ICommandExecutor
{
    private readonly IReadOnlyList<InvocationMiddleware> _middlewares;
    private readonly IReadOnlyList<ExceptionMapper> _exceptionMappers;
    private readonly ILoggerFactory _loggerFactory;

    public DefaultCommandExecutor(
        IEnumerable<InvocationMiddleware> middlewares,
        IEnumerable<ExceptionMapper> exceptionMappers,
        ILoggerFactory loggerFactory)
    {
        _middlewares = middlewares.ToArray();
        // User-registered mappers get first say; the built-in CommandErrorException
        // mapping stays as the final fallback so it keeps working with zero config.
        _exceptionMappers =
        [
            .. exceptionMappers,
            static (Exception e) => e is CommandErrorException c
                ? new CommandError(c.ExitCode, c.Message, c.MessageArguments)
                : null,
        ];
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
        catch (Exception e) when (Map(e) is { } error)
        {
            context.ExitCode = error.ExitCode;
            var logger = _loggerFactory.CreateLogger(context.CommandType.FullName ?? context.CommandType.Name);
            logger.LogError(error.MessageTemplate, error.MessageArguments);
        }
    }

    private CommandError? Map(Exception exception)
    {
        foreach (var mapper in _exceptionMappers)
        {
            if (mapper(exception) is { } error)
            {
                return error;
            }
        }
        return null;
    }
}
