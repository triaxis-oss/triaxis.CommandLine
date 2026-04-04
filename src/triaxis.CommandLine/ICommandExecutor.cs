namespace triaxis.CommandLine;

public interface ICommandExecutor
{
    Task ExecuteAsync(InvocationContext context, Func<Task> command);
}
