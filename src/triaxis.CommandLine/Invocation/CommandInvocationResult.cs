namespace triaxis.CommandLine.Invocation;

using System.CommandLine.Invocation;

public abstract class CommandInvocationResult : ICommandInvocationResult
{
    public abstract Task EnsureCompleteAsync(CancellationToken cancellationToken);

    public void Apply(InvocationContext context)
    {
        EnsureCompleteAsync(context.GetCancellationToken()).GetAwaiter().GetResult();
    }

    public static ICommandInvocationResult Create(object result, Type resultType)
    {
        if (resultType.IsGenericType)
        {
            var resultGenType = resultType.GetGenericTypeDefinition();
            if (resultGenType == typeof(Task<>))
            {
                var args = resultType.GetGenericArguments();
                if (args[0].GetIEnumerableElementType() is { } elementType)
                {
                    return (ICommandInvocationResult)Activator.CreateInstance(typeof(AsyncIEnumerableCommandInvocationResult<>).MakeGenericType(elementType), result);
                }
                return (ICommandInvocationResult)Activator.CreateInstance(typeof(AsyncValueCommandInvocationResult<>).MakeGenericType(args), result);
            }
            if (resultGenType == typeof(IAsyncEnumerable<>))
            {
                return (ICommandInvocationResult)Activator.CreateInstance(typeof(AsyncEnumerableCommandInvocationResult<>).MakeGenericType(resultType.GetGenericArguments()), result);
            }
        }

        if (result is Task task)
        {
            return new AsyncEmptyCommandInvocationResult(task);
        }

        if (result == null)
        {
            return new EmptyCommandInvocationResult();
        }

        if (resultType.GetIEnumerableElementType() is { } elementType2)
        {
            return (ICommandInvocationResult)Activator.CreateInstance(typeof(EnumerableCommandInvocationResult<>).MakeGenericType(elementType2), result);
        }
        return (ICommandInvocationResult)Activator.CreateInstance(typeof(ValueCommandInvocationResult<>).MakeGenericType(resultType), result);
    }
}
