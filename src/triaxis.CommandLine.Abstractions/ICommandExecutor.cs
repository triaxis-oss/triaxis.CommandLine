namespace triaxis.CommandLine;

using System.CommandLine.Invocation;

public interface ICommandExecutor
{
    Task<IInvocationResult?> ExecuteCommandAsync(Type command);
    bool HandleError(InvocationContext context, Exception exception);
}
