namespace triaxis.CommandLine.Invocation;

public class EmptyCommandInvocationResult : ICommandInvocationResult
{
    public Task EnsureCompleteAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
