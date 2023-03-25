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
    private readonly ILogger<DependencyCommandExecutor> _logger;

    public DependencyCommandExecutor(
        IServiceProvider serviceProvider,
        IPropertyInjector propertyInjector,
        InvocationContext context,
        ILogger<DependencyCommandExecutor> logger)
    {
        _serviceProvider = serviceProvider;
        _propertyInjector = propertyInjector;
        _context = context;
        _logger = logger;
    }

    public async Task<IInvocationResult?> ExecuteCommandAsync(Type command)
    {
        _logger.LogTrace("Creating command of type {CommandType}...", command);
        var instance = _serviceProvider.GetRequiredService(command);

        var (d, cancellable) = CreateDelegate(instance, command);

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

        var task = d(cancellationToken);
        await task;
        return (task as Task<IInvocationResult>)?.Result;
    }

    private (Func<CancellationToken, Task>, bool) CreateDelegate(object instance, Type command)
    {
        if (instance.GetType().GetMethod("ExecuteAsync", Type.EmptyTypes) is MethodInfo mth)
        {
            if (Delegate.CreateDelegate(typeof(Func<Task>), instance, mth, false) is Func<Task> d)
            {
                _logger.LogTrace("Found delegate returning a Task: {TaskType}", mth.ReturnType);
                return (ct => d(), false);
            }
        }
        else if (instance.GetType().GetMethod("ExecuteAsync", new[] { typeof(CancellationToken) }) is MethodInfo mthCt)
        {
            if (Delegate.CreateDelegate(typeof(Func<CancellationToken, Task>), instance, mthCt, false) is Func<CancellationToken, Task> dCt)
            {
                _logger.LogTrace("Found cancellable delegate returning a Task: {TaskType}", mthCt.ReturnType);
                return (dCt, true);
            }
        }
        else
        {
            throw new InvalidProgramException($"Command type {command.FullName} does not implement the ExecuteAsync() method");
        }

        throw new InvalidProgramException($"Failed to bind exec method for command {command.FullName}");
    }
}
