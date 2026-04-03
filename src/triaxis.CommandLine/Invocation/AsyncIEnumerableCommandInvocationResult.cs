namespace triaxis.CommandLine.Invocation;

public class AsyncIEnumerableCommandInvocationResult<T> : CommandInvocationResult, ICommandInvocationResult<T>
{
    Task<IEnumerable<T>>? _task;

    public AsyncIEnumerableCommandInvocationResult(Task<IEnumerable<T>> task)
    {
        _task = task;
    }

    public bool IsCollection => true;

    public override Task EnsureCompleteAsync(CancellationToken cancellationToken)
    {
        return Interlocked.Exchange(ref _task, null) ?? Task.CompletedTask;
    }

    public async Task EnumerateResultsAsync(Func<T, ValueTask> processElement, Func<ValueTask>? flushHint, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _task, null) is { } task)
        {
            foreach (var e in await task)
            {
                await processElement(e);
            }
        }
    }
}
