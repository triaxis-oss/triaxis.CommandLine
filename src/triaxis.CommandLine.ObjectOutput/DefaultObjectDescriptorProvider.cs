namespace triaxis.CommandLine.ObjectOutput;

using System.Data;
using System.Runtime.CompilerServices;

public class DefaultObjectDescriptorProvider<T> : IObjectDescriptorProvider<T>
{
    public IObjectDescriptor GetDescriptor(T? instance)
    {
        if (ObjectDescriptorRegistry.TryGet(typeof(T)) is { } generated)
        {
            return generated;
        }

        // Every branch below discovers shape at run time, so they all have to sit behind
        // the switch: leaving even one reachable — the tuple path did — keeps the whole
        // reflective graph alive and the trimmer cannot drop any of it.
        if (!ObjectOutputFeatures.ReflectionFallbackEnabled)
        {
            throw new NotSupportedException(
                $"No generated object descriptor for '{typeof(T)}', and the reflective fallback is disabled. " +
                "Return the type from a command so the source generator describes it, or re-enable " +
                "<EnableObjectOutputReflectionFallback>.");
        }

        if (instance is DataTable t)
        {
            return new DataTableDescriptor(t);
        }

#if NETSTANDARD2_1_OR_GREATER
        if (instance is ITuple)
        {
            return TupleObjectDescriptor<T>.Instance;
        }
#endif

        return SimpleObjectDescriptor<T>.Instance;
    }
}
