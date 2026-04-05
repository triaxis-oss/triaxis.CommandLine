namespace triaxis.CommandLine;

using System.CommandLine.Parsing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

class ToolHost(IServiceProvider services, ParseResult parseResult) : IHost
{
    private IHostedService[]? _hostedServices;

    public IServiceProvider Services => services;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _hostedServices = services.GetServices<IHostedService>().ToArray();
        foreach (var service in _hostedServices)
        {
            await service.StartAsync(cancellationToken);
        }
    }

    public void Start()
    {
        StartAsync().GetAwaiter().GetResult();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_hostedServices is null)
        {
            return;
        }

        for (var i = _hostedServices.Length - 1; i >= 0; i--)
        {
            await _hostedServices[i].StopAsync(cancellationToken);
        }
    }

    public int Invoke() => parseResult.Invoke();

    public Task<int> InvokeAsync() => parseResult.InvokeAsync();

    public void Dispose()
    {
        (services as IDisposable)?.Dispose();
    }
}
