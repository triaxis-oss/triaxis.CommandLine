# Hosting integration

`IToolBuilder` implements `IHostBuilder`, and `ToolHost` implements `IHost`. That means a
`triaxis.CommandLine` tool is, to any consumer of `Microsoft.Extensions.Hosting`, just a
host — one whose "work" happens to be running a single command via `ParseResult.Invoke`
between `StartAsync` and `StopAsync`.

This document describes the conformance surface, the lifecycle, and the
`HostBuilderContext.GetInvocationContext()` bridge that lets configuration callbacks read
parsed command-line arguments.

## `IToolBuilder : IHostBuilder`

```csharp
public interface IToolBuilder : IHostBuilder
{
    string[] Arguments { get; }
    RootCommand RootCommand { get; }
    IConfigurationManager Configuration { get; }
    Command GetCommand(params string[] path);
    IToolBuilder AddMiddleware(InvocationMiddleware middleware);
    IToolBuilder ConfigureServices(Action<IServiceCollection> configure);
    Func<IServiceProvider> GetServiceProviderAccessor();
}
```

Because `IToolBuilder` extends `IHostBuilder`, you can call any standard hosting extension
without casting:

```csharp
Tool.CreateBuilder(args)
    .ConfigureAppConfiguration((ctx, config) =>
    {
        config.AddJsonFile("appsettings.json", optional: true);
        config.AddEnvironmentVariables("MYTOOL_");
    })
    .ConfigureServices((ctx, services) =>
    {
        services.Configure<MyOptions>(ctx.Configuration.GetSection("My"));
        services.AddHostedService<BackgroundCleanup>();
    })
    .UseSerilog((ctx, logger) => logger.ReadFrom.Configuration(ctx.Configuration))
    .AddCommandsFromAssembly()
    .Run();
```

There are **two** `ConfigureServices` overloads:

| Overload | When it runs | Access to `HostBuilderContext`? |
| --- | --- | --- |
| `IToolBuilder.ConfigureServices(Action<IServiceCollection>)` | Immediately, when called. | No. |
| `IHostBuilder.ConfigureServices(Action<HostBuilderContext, IServiceCollection>)` | Deferred until `Build()`. | Yes. |

Both are composable — call them as many times as you want. The deferred overload sees the
fully assembled `HostBuilderContext`, including the build-time `InvocationContext` (see
below).

### What is *not* supported

`ToolBuilder` throws `NotSupportedException` for `UseServiceProviderFactory` and
`ConfigureContainer` — the DI container is always the built-in
`Microsoft.Extensions.DependencyInjection.ServiceProvider`.

## `Run` / `RunAsync` are extension methods

The `IToolBuilder` interface itself is now purely a configuration surface. `Run` and
`RunAsync` are extension methods that go through the full host lifecycle:

```csharp
public static int Run(this IToolBuilder builder)
{
    using var host = builder.Build();
    host.Start();
    try { return ((ToolHost)host).Invoke(); }
    finally { host.StopAsync().GetAwaiter().GetResult(); }
}

public static async Task<int> RunAsync(this IToolBuilder builder)
{
    using var host = builder.Build();
    await host.StartAsync();
    try { return await ((ToolHost)host).InvokeAsync(); }
    finally { await host.StopAsync(); }
}
```

If you want finer-grained control, call `Build()` yourself:

```csharp
using var host = Tool.CreateBuilder(args).UseDefaults().Build();
await host.StartAsync();
try
{
    var exitCode = await ((ToolHost)host).InvokeAsync();
    // do extra work with host.Services here
    return exitCode;
}
finally
{
    await host.StopAsync();
}
```

## `ToolHost`

```csharp
class ToolHost(IServiceProvider services, ParseResult parseResult) : IHost
{
    public IServiceProvider Services => services;

    public Task StartAsync(CancellationToken cancellationToken = default);
    public void Start();
    public Task StopAsync(CancellationToken cancellationToken = default);

    public int Invoke();            // parseResult.Invoke()
    public Task<int> InvokeAsync(); // parseResult.InvokeAsync()

    public void Dispose();          // disposes the ServiceProvider
}
```

Lifecycle:

1. `Build()` constructs `ToolHost(provider, parseResult)`.
2. `StartAsync` enumerates `IHostedService` instances from DI and calls `StartAsync` on
   each in registration order.
3. `Invoke` / `InvokeAsync` runs the command via `ParseResult.Invoke`/`InvokeAsync`.
4. `StopAsync` calls `StopAsync` on every hosted service in **reverse** order.
5. `Dispose` disposes the `ServiceProvider`, which in turn disposes any disposable
   services (loggers, HTTP handlers, etc.).

Any exception thrown during `Invoke` / `InvokeAsync` escapes to `Run` / `RunAsync`, which
still runs `StopAsync` in a `finally`. Hosted services therefore get a chance to shut down
cleanly even when a command fails.

### `IHostedService` support

Hosted services are resolved and started in DI-registration order before the command
runs, and stopped in reverse order after it returns — the standard
`Microsoft.Extensions.Hosting` contract.

```csharp
builder.ConfigureServices((ctx, s) =>
{
    s.AddHostedService<MetricsServer>();
    s.AddHostedService<ConfigWatcher>();
});
```

## `HostBuilderContext.GetInvocationContext()`

Configuration callbacks often need to branch on command-line arguments — verbosity,
profiles, environments, file paths. At configuration time the service provider doesn't
exist yet, but the `ParseResult` already does (parsing is the first thing `Build()` does).

To expose it, `Build()` stores a build-time `InvocationContext` in
`HostBuilderContext.Properties` under a known key, and provides an extension method in
the `Microsoft.Extensions.Hosting` namespace:

```csharp
namespace Microsoft.Extensions.Hosting;

public static class HostBuilderContextExtensions
{
    public static InvocationContext GetInvocationContext(this HostBuilderContext context);
}
```

The namespace deliberately matches what the legacy `System.CommandLine.Hosting` package
used, so existing consumer code works without extra usings.

Usage:

```csharp
using Microsoft.Extensions.Hosting;

builder.ConfigureAppConfiguration((ctx, config) =>
{
    var parse = ctx.GetInvocationContext().ParseResult;
    var env = parse.GetValue<string>("--environment") ?? "Production";
    config.AddJsonFile($"appsettings.{env}.json", optional: true);
});
```

Only `ParseResult` is populated at build time — `Services` and `CommandType` are `null`
until a command actually runs. The "real" `InvocationContext` that flows through
middleware is a different instance, created later by the generated command action.

## Build-time vs runtime `InvocationContext`

Two distinct `InvocationContext` instances exist during the lifetime of a tool:

| | Build-time (config callbacks) | Runtime (middleware + commands) |
| --- | --- | --- |
| Where it lives | `HostBuilderContext.Properties` | Created by the generated command action |
| `ParseResult` | ✅ populated | ✅ populated |
| `Services` | ❌ null | ✅ the runtime service provider |
| `CommandType` | ❌ null | ✅ the command being executed |
| `CancellationToken` | n/a | Ctrl+C token from System.CommandLine |
| `InvocationResult` / `ExitCode` | n/a | Populated by the command + executor |

## `UseSerilog` and `HostBuilderContext`

`UseSerilog` exposes three overloads:

```csharp
IToolBuilder UseSerilog(this IToolBuilder builder,
    bool useShortContext = false,
    Action<IConfiguration, LoggerConfiguration>? configure = null);

IToolBuilder UseSerilog(this IToolBuilder builder,
    Action<HostBuilderContext, LoggerConfiguration> configure);

IToolBuilder UseSerilog(this IToolBuilder builder,
    bool useShortContext,
    Action<HostBuilderContext, LoggerConfiguration>? configure);
```

The `HostBuilderContext` overload is the primary shape. It is implemented by capturing the
context through `IHostBuilder.ConfigureServices(HostBuilderContext, IServiceCollection)` so
the Serilog factory can close over it — `HostBuilderContext` is a build-time object that
shouldn't leak into the runtime container. The `IConfiguration` overload is kept for
source compatibility and routes through the `HostBuilderContext` one internally.
