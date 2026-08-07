namespace triaxis.CommandLine.ObjectOutput;

using System.Data;

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

        if (instance is not null && TupleTypes.IsTuple(instance.GetType()))
        {
            return TupleObjectDescriptor<T>.Instance;
        }

        return SimpleObjectDescriptor<T>.Instance;
    }
}
