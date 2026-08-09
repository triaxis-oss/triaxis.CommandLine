namespace triaxis.CommandLine.ObjectOutput;

public class DynamicObjectOutputHandler : IObjectOutputHandler
{
    private readonly IServiceProvider _serviceProvider;

    public DynamicObjectOutputHandler(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public Task ProcessOutputAsync(ICommandInvocationResult cir, CancellationToken cancellationToken)
    {
        var elementType = cir.GetType().GetInterfaces()
            .Single(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ICommandInvocationResult<>))
            .GetGenericArguments()[0];

        var typedHandler = ToolBuilderObjectOutputExtensions.ResolveHandler(_serviceProvider, elementType)
            ?? throw new InvalidOperationException(
                $"No object output handler for '{elementType}'. Return the type from a command so the " +
                "source generator emits one, or re-enable <EnableObjectOutputReflectionFallback>.");

        return typedHandler.ProcessOutputAsync(cir, cancellationToken);
    }
}
