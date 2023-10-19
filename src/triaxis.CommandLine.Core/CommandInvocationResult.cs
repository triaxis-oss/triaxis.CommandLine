namespace triaxis.CommandLine;

using System.CommandLine.Invocation;

internal abstract class CommandInvocationResult : ICommandInvocationResult
{
    private CommandInvocationResult()
    {
    }

    public abstract Task EnsureCompleteAsync(CancellationToken cancellationToken);

    public void Apply(InvocationContext context)
    {
        EnsureCompleteAsync(context.GetCancellationToken()).GetAwaiter().GetResult();
    }

    internal static ICommandInvocationResult Create(object instance, object result, Type resultType)
    {
        if (resultType.IsGenericType)
        {
            var resultGenType = resultType.GetGenericTypeDefinition();
            if (resultGenType == typeof(Task<>))
            {
                var args = resultType.GetGenericArguments();
                if (args[0].GetIEnumerableElementType() is { } elementType)
                {
                    return (ICommandInvocationResult)Activator.CreateInstance(typeof(AsyncIEnumerable<>).MakeGenericType(elementType), result);
                }
                return (ICommandInvocationResult)Activator.CreateInstance(typeof(Async<>).MakeGenericType(args), result);
            }
            if (resultGenType == typeof(IAsyncEnumerable<>))
            {
                return (ICommandInvocationResult)Activator.CreateInstance(typeof(AsyncEnumerable<>).MakeGenericType(resultType.GetGenericArguments()), result);
            }
        }

        if (result is Task task)
        {
            return new Async(task);
        }

        if (result == null)
        {
            return new Sync();
        }

        if (resultType.GetIEnumerableElementType() is { } elementType2)
        {
            return (ICommandInvocationResult)Activator.CreateInstance(typeof(SyncIEnumerable<>).MakeGenericType(elementType2), result);
        }
        return (ICommandInvocationResult)Activator.CreateInstance(typeof(Sync<>).MakeGenericType(resultType), result);
    }

    class Sync : CommandInvocationResult
    {
        public override Task EnsureCompleteAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    class Sync<T> : CommandInvocationResult, ICommandInvocationResult<T>
    {
        T _result;

        public Sync(T result)
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

    class SyncIEnumerable<T> : CommandInvocationResult, ICommandInvocationResult<T>
    {
        IEnumerable<T> _result;

        public SyncIEnumerable(IEnumerable<T> result)
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

    class Async : CommandInvocationResult
    {
        Task _task;

        public Async(Task task)
        {
            _task = task;
        }

        public override Task EnsureCompleteAsync(CancellationToken cancellationToken)
        {
            return _task;
        }
    }

    class Async<T> : CommandInvocationResult, ICommandInvocationResult<T>
    {
        Task<T> _task;

        public Async(Task<T> task)
        {
            _task = task;
        }

        public bool IsCollection => false;

        public override Task EnsureCompleteAsync(CancellationToken cancellationToken)
        {
            return _task;
        }

        public async Task EnumerateResultsAsync(Func<T, ValueTask> processElement, Func<ValueTask>? flushHint, CancellationToken cancellationToken)
        {
            await processElement(await _task);
        }
    }

    class AsyncIEnumerable<T> : CommandInvocationResult, ICommandInvocationResult<T>
    {
        Task<IEnumerable<T>> _task;

        public AsyncIEnumerable(Task<IEnumerable<T>> task)
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

    class AsyncEnumerable<T> : CommandInvocationResult, ICommandInvocationResult<T>
    {
        IAsyncEnumerable<T>? _enumerable;

        public AsyncEnumerable(IAsyncEnumerable<T> enumerable)
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
}
