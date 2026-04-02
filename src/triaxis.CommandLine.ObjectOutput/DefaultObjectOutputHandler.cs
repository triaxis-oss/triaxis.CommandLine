namespace triaxis.CommandLine.ObjectOutput;

public class DefaultObjectOutputHandler<T> : IObjectOutputHandler<T>
{
    private readonly IObjectDescriptorProvider<T> _descriptorProvider;
    private readonly IObjectFormatterProvider _formatterProvider;
    private readonly IOutputStreamProvider _outputStreamProvider;

    public DefaultObjectOutputHandler(IObjectDescriptorProvider<T> descriptorProvider, IObjectFormatterProvider formatterProvider, IOutputStreamProvider outputStreamProvider)
    {
        _descriptorProvider = descriptorProvider;
        _formatterProvider = formatterProvider;
        _outputStreamProvider = outputStreamProvider;
    }

    public async Task ProcessOutputAsync(ICommandInvocationResult<T> cir, CancellationToken cancellationToken)
    {
        IObjectFormatter<T>? formatter = null;
        TextWriter? output = null;

        try
        {
            await cir.EnumerateResultsAsync(async e =>
            {
                if (e is not null)
                {
                    if (formatter is null)
                    {
                        var descriptor = _descriptorProvider.GetDescriptor(e);
                        output = _outputStreamProvider.GetOutputStream();
                        formatter = await _formatterProvider.CreateFormatterAsync<T>(descriptor, output, cir.IsCollection);
                    }
                    await formatter.OutputElementAsync(e);
                }
            }, flushHint: async () =>
            {
                if (output is not null)
                {
                    await output.FlushAsync();
                }
            }, cancellationToken);
        }
        finally
        {
            if (formatter is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync();
            }
            if (output is not null)
            {
                await output.FlushAsync();
            }
        }
    }

    Task IObjectOutputHandler.ProcessOutputAsync(ICommandInvocationResult cir, CancellationToken cancellationToken)
        => ProcessOutputAsync((ICommandInvocationResult<T>)cir, cancellationToken);
}
