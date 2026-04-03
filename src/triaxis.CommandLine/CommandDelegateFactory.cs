namespace triaxis.CommandLine;

using System.Reflection;

static class CommandDelegateFactory
{
    public static (Func<CancellationToken, object> invoke, bool cancellable, Type resultType) CreateDelegate(object instance, Type command)
    {
        if (command.GetMethod("ExecuteAsync", new[] { typeof(CancellationToken) }) is MethodInfo mthCt)
        {
            if (Delegate.CreateDelegate(typeof(Func<CancellationToken, object>), instance, mthCt, false) is Func<CancellationToken, object> dCt)
            {
                return (dCt, true, mthCt.ReturnType);
            }
        }

        if ((command.GetMethod("ExecuteAsync", Type.EmptyTypes) ?? command.GetMethod("Execute", Type.EmptyTypes)) is MethodInfo mth)
        {
            if (Delegate.CreateDelegate(typeof(Func<object>), instance, mth, false) is Func<object> d)
            {
                return (ct => d(), false, mth.ReturnType);
            }
        }

        throw new InvalidProgramException($"Command type {command.FullName} does not implement the ExecuteAsync() method");
    }
}
