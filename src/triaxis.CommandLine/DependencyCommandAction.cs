namespace triaxis.CommandLine;

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

public class DependencyCommandAction : AsynchronousCommandLineAction
{
    private readonly Func<IServiceProvider> _getServiceProvider;

    public Type CommandType { get; }

    internal DependencyCommandAction(Func<IServiceProvider> getServiceProvider, Type commandType)
    {
        _getServiceProvider = getServiceProvider;
        CommandType = commandType;
    }

    public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var provider = _getServiceProvider();
        var context = new InvocationContext(provider, parseResult, cancellationToken, CommandType);

        await provider.GetRequiredService<ICommandExecutor>().ExecuteAsync(context, () => ExecuteCoreAsync(context));

        return context.ExitCode;
    }

    private async Task ExecuteCoreAsync(InvocationContext context)
    {
        var provider = context.Services;
        var instance = provider.GetRequiredService(CommandType);

        provider.GetRequiredService<IPropertyInjector>().InjectProperties(instance);
        BindCommandParameters(instance, context.ParseResult);

        var (invoke, cancellable, resultType) = CreateDelegate(instance);

        CancellationTokenRegistration failFastRegistration = default;
        if (!cancellable)
        {
            failFastRegistration = context.GetCancellationToken().Register(() => Environment.FailFast(null));
        }

        try
        {
            var rawResult = invoke(context.GetCancellationToken());

            if (rawResult is Task<ICommandInvocationResult?> task)
            {
                context.InvocationResult = await task;
            }
            else
            {
                context.InvocationResult = Invocation.CommandInvocationResult.Create(rawResult, resultType);
            }
        }
        finally
        {
            failFastRegistration.Dispose();
        }
    }

    private void BindCommandParameters(object instance, ParseResult parseResult)
    {
        var cmd = parseResult.CommandResult.Command;
        var type = instance.GetType();

        foreach (var arg in cmd.Arguments.OfType<IMemberBoundSymbol>())
        {
            if (arg.GetRootMember().DeclaringType?.IsAssignableFrom(type) == true)
            {
                if (parseResult.GetResult((Argument)arg) is { } res && res.Tokens.Any())
                {
                    arg.SetValue(instance, res);
                }
            }
        }

        foreach (var opt in cmd.Options.OfType<IMemberBoundSymbol>())
        {
            if (opt.GetRootMember().DeclaringType?.IsAssignableFrom(type) == true)
            {
                if (parseResult.GetResult((Option)opt) is { } res && !res.Implicit)
                {
                    opt.SetValue(instance, res);
                }
            }
        }
    }

    private (Func<CancellationToken, object> invoke, bool cancellable, Type resultType) CreateDelegate(object instance)
    {
        if (CommandType.GetMethod("ExecuteAsync", [typeof(CancellationToken)]) is MethodInfo mthCt)
        {
            if (Delegate.CreateDelegate(typeof(Func<CancellationToken, object>), instance, mthCt, false) is Func<CancellationToken, object> dCt)
            {
                return (dCt, true, mthCt.ReturnType);
            }
        }

        if ((CommandType.GetMethod("ExecuteAsync", Type.EmptyTypes) ?? CommandType.GetMethod("Execute", Type.EmptyTypes)) is MethodInfo mth)
        {
            if (Delegate.CreateDelegate(typeof(Func<object>), instance, mth, false) is Func<object> d)
            {
                return (ct => d(), false, mth.ReturnType);
            }
        }

        throw new InvalidProgramException($"Command type {CommandType.FullName} does not implement the ExecuteAsync() method");
    }
}
