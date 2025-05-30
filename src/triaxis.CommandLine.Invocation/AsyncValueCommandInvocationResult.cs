namespace triaxis.CommandLine.Invocation;

public class AsyncValueCommandInvocationResult<T> : CommandInvocationResult, ICommandInvocationResult<T>
{
    Task<T>? _task;

    public AsyncValueCommandInvocationResult(Task<T> task)
    {
        _task = task;
    }

    public bool IsCollection => false;

    public override Task EnsureCompleteAsync(CancellationToken cancellationToken)
    {
        return Interlocked.Exchange(ref _task, null) ?? Task.CompletedTask;
    }

    public async Task EnumerateResultsAsync(Func<T, ValueTask> processElement, Func<ValueTask>? flushHint, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _task, null) is { } task)
        {
            await processElement(await task);
        }
    }
}
