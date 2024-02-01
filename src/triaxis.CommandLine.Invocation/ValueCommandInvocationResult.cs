namespace triaxis.CommandLine.Invocation;

public class ValueCommandInvocationResult<T> : CommandInvocationResult, ICommandInvocationResult<T>
{
    T _result;

    public ValueCommandInvocationResult(T result)
    {
        _result = result;
    }

    public bool IsCollection => false;

    public override Task EnsureCompleteAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public async Task EnumerateResultsAsync(Func<T, ValueTask> processElement, Func<ValueTask>? flushHint, CancellationToken cancellationToken)
    {
        await processElement(_result);
    }
}
