namespace Microsoft.Extensions.Hosting;

using System.Diagnostics.CodeAnalysis;

public static class HostBuilderContextExtensions
{
    public static void SetContextProperty<T>(this HostBuilderContext context, T value)
    {
        if (context.TryGetContextProperty<Action<T>>(out var notify))
        {
            notify(value);
        }
        context.Properties[typeof(T)] = value;
    }

    public static bool TryGetContextProperty<T>(this HostBuilderContext context, [NotNullWhen(true)] out T? value)
    {
        if (context.Properties.TryGetValue(typeof(T), out var oValue) && oValue is T tValue)
        {
            value = tValue;
            return true;
        }
        else
        {
            value = default;
            return false;
        }
    }

    public static void ObserveContextProperty<T>(this HostBuilderContext context, Action<T> notify)
    {
        if (context.TryGetContextProperty<T>(out var val))
        {
            notify(val);
        }
        if (context.TryGetContextProperty<Action<T>>(out var prev))
        {
            notify = prev + notify;
        }
        context.SetContextProperty(notify);
    }
}
