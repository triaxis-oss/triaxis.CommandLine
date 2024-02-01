namespace triaxis.CommandLine.Invocation;

public class AsyncIEnumerableCommandInvocationResult<T> : CommandInvocationResult, ICommandInvocationResult<T>
{
    Task<IEnumerable<T>> _task;

    public AsyncIEnumerableCommandInvocationResult(Task<IEnumerable<T>> task)
    {
        _task = task;
    }

    public bool IsCollection => true;

    public override Task EnsureCompleteAsync(CancellationToken cancellationToken)
    {
        return _task;
    }

    public async Task EnumerateResultsAsync(Func<T, ValueTask> processElement, Func<ValueTask>? flushHint, CancellationToken cancellationToken)
    {
        foreach (var e in await _task)
        {
            await processElement(e);
        }
    }
}
