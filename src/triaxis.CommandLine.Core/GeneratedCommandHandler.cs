namespace triaxis.CommandLine;

using System.CommandLine.Hosting;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// A command handler produced by source-generated registration code.
/// Unlike <c>DependencyCommandHandler</c>, it accepts a compiled binder delegate
/// that sets argument/option values on the command instance without runtime assembly
/// scanning or <c>IMemberBoundSymbol</c> reflection.
/// </summary>
public sealed class GeneratedCommandHandler : ICommandHandler
{
    private readonly Action<object, ParseResult>? _binder;

    public Type CommandType { get; }

    public GeneratedCommandHandler(Type commandType, Action<object, ParseResult>? binder = null)
    {
        CommandType = commandType;
        _binder = binder;
    }

    public int Invoke(InvocationContext context)
        => InvokeAsync(context).GetAwaiter().GetResult();

    public async Task<int> InvokeAsync(InvocationContext context)
    {
        var host = context.GetHost();
        context.InvocationResult = await host.Services
            .GetRequiredService<ICommandExecutor>()
            .ExecuteCommandAsync(CommandType, _binder);
        return 0;
    }
}
