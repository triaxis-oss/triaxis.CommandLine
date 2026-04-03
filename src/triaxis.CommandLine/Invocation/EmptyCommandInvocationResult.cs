namespace triaxis.CommandLine.Invocation;

public class EmptyCommandInvocationResult : CommandInvocationResult
{
    public override Task EnsureCompleteAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
