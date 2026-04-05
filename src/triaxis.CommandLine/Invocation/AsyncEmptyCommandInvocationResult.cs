namespace triaxis.CommandLine.Invocation;

public class AsyncEmptyCommandInvocationResult : ICommandInvocationResult
{
    Task? _task;

    public AsyncEmptyCommandInvocationResult(Task task)
    {
        _task = task;
    }

    public Task EnsureCompleteAsync(CancellationToken cancellationToken)
    {
        return Interlocked.Exchange(ref _task, null) ?? Task.CompletedTask;
    }
}
