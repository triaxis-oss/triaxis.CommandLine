namespace triaxis.CommandLine;

using System.CommandLine.Invocation;
using System.CommandLine.Parsing;

public interface ICommandExecutor
{
    Task<IInvocationResult?> ExecuteCommandAsync(Type command);
    Task<IInvocationResult?> ExecuteCommandAsync(Type command, Action<object, ParseResult>? binder);
    bool HandleError(InvocationContext context, Exception exception);
}
