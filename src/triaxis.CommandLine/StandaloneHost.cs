namespace triaxis.CommandLine;

using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Minimal <see cref="IHost"/> returned by <c>ToolBuilder.Build()</c> when the matched
/// command's action implements <see cref="IStandaloneAction"/>. No service provider is
/// built; <c>Start</c>/<c>Stop</c> are no-ops. The command owns its own lifecycle and
/// optionally builds its own host via <see cref="IToolBuilder.ApplyTo"/>.
/// </summary>
sealed class StandaloneHost(IToolBuilder builder, IStandaloneAction action, ParseResult parseResult) : IHost
{
    // No CLI-side services are built; commands that need config/DI build their own host
    // and call builder.ApplyTo(target) to inherit the builder's registrations.
    public IServiceProvider Services { get; } = new ServiceCollection().BuildServiceProvider();

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public int Invoke()
        => InvokeAsync().GetAwaiter().GetResult();

    public Task<int> InvokeAsync(CancellationToken cancellationToken = default)
        => action.InvokeAsync(builder, parseResult, cancellationToken);

    public void Dispose()
    {
        (Services as IDisposable)?.Dispose();
    }
}
