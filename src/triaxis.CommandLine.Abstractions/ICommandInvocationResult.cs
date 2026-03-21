namespace triaxis.CommandLine;

public interface ICommandInvocationResult
{
    Task EnsureCompleteAsync(CancellationToken cancellationToken);
}

public interface ICommandInvocationResult<T> : ICommandInvocationResult
{
    Task EnumerateResultsAsync(Func<T, ValueTask> processElement, Func<ValueTask>? flushHint, CancellationToken cancellationToken);
    bool IsCollection { get; }
}
