namespace triaxis.CommandLine;

using triaxis.CommandLine.Invocation;

public static class CommandInvocationResultFactoryExtensions
{
    public static ICommandInvocationResult<T> ToCommandInvocationResult<T>(this Task<IEnumerable<T>> taskReturningEnumerable)
        => new AsyncIEnumerableCommandInvocationResult<T>(taskReturningEnumerable);

    public static ICommandInvocationResult<T> ToCommandInvocationResult<T>(this IAsyncEnumerable<T> asyncEnumerable)
        => new AsyncEnumerableCommandInvocationResult<T>(asyncEnumerable);

    public static ICommandInvocationResult<T> ToCommandInvocationResult<T>(this Task<T> task)
        => new AsyncValueCommandInvocationResult<T>(task);

    public static ICommandInvocationResult<T> ToCommandInvocationResult<T>(this IEnumerable<T> enumerable)
        => new EnumerableCommandInvocationResult<T>(enumerable);

    public static ICommandInvocationResult<T> ToCommandInvocationResult<T>(this T value)
        => new ValueCommandInvocationResult<T>(value);
}
