namespace triaxis.CommandLine.ObjectOutput;

using Microsoft.Extensions.DependencyInjection;

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

        var typedHandler = (IObjectOutputHandler)_serviceProvider.GetRequiredService(typeof(IObjectOutputHandler<>).MakeGenericType(elementType));
        return typedHandler.ProcessOutputAsync(cir, cancellationToken);
    }
}
