namespace triaxis.CommandLine;

using System.CommandLine.Parsing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

class ToolHost(IServiceProvider services, ParseResult parseResult) : IHost, IHostApplicationLifetime, IAsyncDisposable
{
    private IHostedService[]? _hostedServices;
    private readonly CancellationTokenSource _startedSource = new();
    private readonly CancellationTokenSource _stoppingSource = new();
    private readonly CancellationTokenSource _stoppedSource = new();

    public IServiceProvider Services => services;

    public CancellationToken ApplicationStarted => _startedSource.Token;
    public CancellationToken ApplicationStopping => _stoppingSource.Token;
    public CancellationToken ApplicationStopped => _stoppedSource.Token;

    public void StopApplication()
    {
        _stoppingSource.Cancel();
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _hostedServices = services.GetServices<IHostedService>().ToArray();
        foreach (var service in _hostedServices)
        {
            await service.StartAsync(cancellationToken);
        }

        _startedSource.Cancel();
    }

    public void Start()
    {
        StartAsync().GetAwaiter().GetResult();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _stoppingSource.Cancel();

        if (_hostedServices is not null)
        {
            for (var i = _hostedServices.Length - 1; i >= 0; i--)
            {
                await _hostedServices[i].StopAsync(cancellationToken);
            }
        }

        _stoppedSource.Cancel();
    }

    public int Invoke() => parseResult.Invoke();

    public Task<int> InvokeAsync() => parseResult.InvokeAsync();

    public void Dispose()
    {
        _startedSource.Dispose();
        _stoppingSource.Dispose();
        _stoppedSource.Dispose();
        (services as IDisposable)?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _startedSource.Dispose();
        _stoppingSource.Dispose();
        _stoppedSource.Dispose();
        await services.AsAsyncDisposable().DisposeAsync();
    }
}
