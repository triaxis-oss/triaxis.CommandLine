namespace triaxis.CommandLine;

using System.CommandLine;

public interface ICommandExecutor
{
    Task<ICommandInvocationResult?> ExecuteCommandAsync(Type command);
    bool HandleError(ParseResult parseResult, Exception exception);
}
