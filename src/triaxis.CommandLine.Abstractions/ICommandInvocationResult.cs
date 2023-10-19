namespace triaxis.CommandLine;

using System.CommandLine.Invocation;

public interface ICommandInvocationResult : IInvocationResult
{
    Task EnsureCompleteAsync(CancellationToken cancellationToken);
}

public interface ICommandInvocationResult<T> : ICommandInvocationResult
{
    Task EnumerateResultsAsync(Func<T, ValueTask> processElement, Func<ValueTask>? flushHint, CancellationToken cancellationToken);
    bool IsCollection { get; }
}
