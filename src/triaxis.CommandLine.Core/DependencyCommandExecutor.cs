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

        var (d, cancellable, resultType) = CreateDelegate(instance, command);

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

    private (Func<CancellationToken, object>, bool, Type) CreateDelegate(object instance, Type command)
    {
        if (command.GetMethod("ExecuteAsync", new[] { typeof(CancellationToken) }) is MethodInfo mthCt)
        {
            if (Delegate.CreateDelegate(typeof(Func<CancellationToken, object>), instance, mthCt, false) is Func<CancellationToken, object> dCt)
            {
                _logger.LogTrace("Found cancellable {MethodName} method returning {ReturnType}", mthCt.Name, mthCt.ReturnType);
                return (dCt, true, mthCt.ReturnType);
            }
        }

        if ((command.GetMethod("ExecuteAsync", Type.EmptyTypes) ?? command.GetMethod("Execute", Type.EmptyTypes)) is MethodInfo mth)
        {
            if (Delegate.CreateDelegate(typeof(Func<object>), instance, mth, false) is Func<object> d)
            {
                _logger.LogTrace("Found non-cancellable {MethodName} method returning {ReturnType}", mth.Name, mth.ReturnType);
                return (ct => d(), false, mth.ReturnType);
            }
        }

        throw new InvalidProgramException($"Command type {command.FullName} does not implement the ExecuteAsync() method");
    }
}
