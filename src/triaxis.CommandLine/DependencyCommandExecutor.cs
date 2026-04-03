namespace triaxis.CommandLine;

using System.CommandLine.Invocation;
using System.Data;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal class DependencyCommandExecutor : ICommandExecutor
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IPropertyInjector _propertyInjector;
    private readonly InvocationContext _context;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DependencyCommandExecutor> _logger;

    public DependencyCommandExecutor(
        IServiceProvider serviceProvider,
        IPropertyInjector propertyInjector,
        InvocationContext context,
        ILoggerFactory loggerFactory,
        ILogger<DependencyCommandExecutor> logger)
    {
        _serviceProvider = serviceProvider;
        _propertyInjector = propertyInjector;
        _context = context;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public async Task<IInvocationResult?> ExecuteCommandAsync(Type command)
    {
        _logger.LogTrace("Creating command of type {CommandType}...", command);
        var instance = _serviceProvider.GetRequiredService(command);

        var (d, cancellable, resultType) = CommandDelegateFactory.CreateDelegate(instance, command);

        _propertyInjector.InjectProperties(instance);

        var cmdline = _context.ParseResult;
        var cmd = cmdline.CommandResult.Command;
        if (cmd != null)
        {
            var type = instance.GetType();
            foreach (var arg in cmd.Arguments.OfType<IMemberBoundSymbol>())
            {
                if (arg.GetRootMember().DeclaringType?.IsAssignableFrom(type) == true)
                {
                    if (cmdline.FindResultFor((Argument)arg) is { } res && res.Tokens.Any())
                    {
                        arg.SetValue(instance, res);
                    }
                }
            }

            foreach (var opt in cmd.Options.OfType<IMemberBoundSymbol>())
            {
                if (opt.GetRootMember().DeclaringType?.IsAssignableFrom(type) == true)
                {
                    if (cmdline.FindResultFor((Option)opt) is { } res && !res.IsImplicit)
                    {
                        opt.SetValue(instance, res);
                    }
                }
            }
        }

        var cancellationToken = _context.GetCancellationToken();

        if (!cancellable)
        {
            cancellationToken.Register(() => Environment.FailFast(null));
        }

        var result = d(cancellationToken);

        if (result is Task<IInvocationResult> task)
        {
            return await task;
        }

        return Invocation.CommandInvocationResult.Create(result, resultType);
    }

    public bool HandleError(InvocationContext context, Exception exception)
    {
        // try to log the error under the command that was executed
        var loggerType = (context.ParseResult.CommandResult.Command.Handler as DependencyCommandHandler)?.CommandType ?? GetType();
        var logger = _loggerFactory.CreateLogger(loggerType);
        if (exception is CommandErrorException ce)
        {
            logger.LogError(ce.Message, ce.MessageArguments);
        }
        else
        {
            logger.LogError(exception, "Error while executing command");
        }
        return true;
    }
}
