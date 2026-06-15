namespace triaxis.CommandLine;

using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Minimal <see cref="IHost"/> returned by <c>ToolBuilder.Build()</c> when the matched
/// command's action implements <see cref="IStandaloneAction"/>. No service provider is
/// built; <c>Start</c>/<c>Stop</c> are no-ops. The command owns its own lifecycle and
/// optionally builds its own host via <see cref="IToolBuilder.ApplyTo"/>.
/// </summary>
sealed class StandaloneHost(IToolBuilder builder, IStandaloneAction action, ParseResult parseResult) : IHost, IAsyncDisposable
{
    // No CLI-side services are built; commands that need config/DI build their own host
    // and call builder.ApplyTo(target) to inherit the builder's registrations.
    public IServiceProvider Services { get; } = new ServiceCollection().BuildServiceProvider();

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public int Invoke()
        => InvokeAsync().GetAwaiter().GetResult();

    public Task<int> InvokeAsync(CancellationToken cancellationToken = default)
    {
        // Hand the action the builder so it can replay configuration via ApplyTo.
        action.Builder = builder;

        // A command that declares a CancellationToken opts into framework cancellation:
        // dispatch through System.CommandLine so its process-termination handling (Ctrl+C /
        // SIGTERM honoring ProcessTerminationTimeout) wires a real token into the action,
        // exactly like ToolHost. A command without one can't observe cancellation, so invoke
        // the action directly and keep the standalone path free of S.CL's invocation pipeline —
        // it fully owns its own lifecycle, just as before this token wiring existed.
        return action.ObservesCancellation
            ? parseResult.InvokeAsync(cancellationToken: cancellationToken)
            : ((AsynchronousCommandLineAction)action).InvokeAsync(parseResult, cancellationToken);
    }

    public void Dispose()
        => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync() => Services.AsAsyncDisposable().DisposeAsync();
}
