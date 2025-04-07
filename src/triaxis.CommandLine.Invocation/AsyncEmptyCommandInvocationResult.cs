namespace triaxis.CommandLine.Invocation;

public class AsyncEmptyCommandInvocationResult : CommandInvocationResult
{
    Task? _task;

    public AsyncEmptyCommandInvocationResult(Task task)
    {
        _task = task;
    }

    public override Task EnsureCompleteAsync(CancellationToken cancellationToken)
    {
        return Interlocked.Exchange(ref _task, null) ?? Task.CompletedTask;
    }
}
