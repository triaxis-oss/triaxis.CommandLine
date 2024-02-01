namespace triaxis.CommandLine.Invocation;

class AsyncEnumerableCommandInvocationResult<T> : CommandInvocationResult, ICommandInvocationResult<T>
{
    IAsyncEnumerable<T>? _enumerable;

    public AsyncEnumerableCommandInvocationResult(IAsyncEnumerable<T> enumerable)
    {
        _enumerable = enumerable;
    }

    public bool IsCollection => true;

    public override async Task EnsureCompleteAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _enumerable, null) is { } enumerable)
        {
            await foreach (var e in enumerable.WithCancellation(cancellationToken))
            {
            }
        }
    }

    public async Task EnumerateResultsAsync(Func<T, ValueTask> processElement, Func<ValueTask>? flushHint, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _enumerable, null) is { } enumerable)
        {
            await using var enumerator = enumerable.WithCancellation(cancellationToken).GetAsyncEnumerator();

            for (; ; )
            {
                var tNext = enumerator.MoveNextAsync();
                if (flushHint is not null && !tNext.GetAwaiter().IsCompleted)
                {
                    await flushHint();
                }
                if (!await tNext)
                {
                    break;
                }
                await processElement(enumerator.Current);
            }
        }
    }
}
