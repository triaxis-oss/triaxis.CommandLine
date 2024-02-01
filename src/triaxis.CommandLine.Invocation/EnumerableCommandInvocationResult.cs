namespace triaxis.CommandLine.Invocation;

public class EnumerableCommandInvocationResult<T> : CommandInvocationResult, ICommandInvocationResult<T>
{
    IEnumerable<T> _result;

    public EnumerableCommandInvocationResult(IEnumerable<T> result)
    {
        _result = result;
    }

    public bool IsCollection => true;

    public override Task EnsureCompleteAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public async Task EnumerateResultsAsync(Func<T, ValueTask> processElement, Func<ValueTask>? flushHint, CancellationToken cancellationToken)
    {
        foreach (var e in _result)
        {
            await processElement(e);
        }
    }
}
