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
    ParseResult Parse();
    IHostBuilder ApplyTo(IHostBuilder target);
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

### Standalone commands (`MainAsync`)

A command can opt out of the CLI-side service provider entirely by declaring `MainAsync`
instead of `ExecuteAsync`/`Execute`. When the matched command is standalone, `Build()`
short-circuits before constructing a service provider — the CLI host is never built,
no middleware runs, and the command takes full responsibility for its own lifecycle.

```csharp
[Command("serve")]
public class ServeCommand
{
    [Option("--port")] public int Port { get; set; } = 5000;

    public async Task<int> MainAsync(IToolBuilder builder, CancellationToken ct)
    {
        var web = WebApplication.CreateBuilder();
        builder.ApplyTo(web.Host);             // replay CLI configuration/services
        web.WebHost.UseUrls($"http://*:{Port}");

        await using var app = web.Build();
        app.MapGet("/", () => "hello");
        await app.RunAsync(ct);
        return 0;
    }
}
```

#### Recognised signatures

`MainAsync` may take any combination of `IToolBuilder` and `CancellationToken`
(in that order), and may return `Task` or `Task<int>`:

```csharp
public Task MainAsync();
public Task MainAsync(CancellationToken ct);
public Task MainAsync(IToolBuilder builder);
public Task MainAsync(IToolBuilder builder, CancellationToken ct);
public Task<int> MainAsync( …same four shapes… );
```

A `Task` return yields exit code `0`; `Task<int>` returns the value.

#### `IToolBuilder.ApplyTo(IHostBuilder)`

`ApplyTo` replays the builder's configuration sources, service registrations, and
deferred `IHostBuilder` callbacks onto any `IHostBuilder` — typically the `Host`
property of a `WebApplicationBuilder`. The replay order mirrors what `ToolBuilder.Build()`
would have done:

1. Direct configuration sources (anything added to `builder.Configuration` via
   `IConfigurationBuilder` APIs).
2. Deferred `ConfigureAppConfiguration` callbacks (e.g. from `UseDefaultConfiguration`).
3. Direct service descriptors (anything added via `builder.ConfigureServices(Action<IServiceCollection>)`),
   plus the current `ParseResult` as a singleton.
4. Deferred `ConfigureServices(HostBuilderContext, IServiceCollection)` callbacks
   (e.g. the Serilog factory from `UseSerilog`).

CLI-only state — the middleware chain, `ICommandExecutor`, `ToolHost` itself — is
intentionally omitted: those concepts make no sense on an alternate host.

`ApplyTo` also seeds the build-time `InvocationContext` into the target's
`IHostBuilder.Properties`, under the same key `ToolBuilder.Build()` uses on its own
host. That means any deferred `IHostBuilder` callback running against the target's
`HostBuilderContext` — including ones added by the consumer on the target side —
can call `ctx.GetInvocationContext()` and observe the parsed command line, just as
they would on the CLI-side host.

#### Constraints and diagnostics

The source generator validates standalone commands and emits errors for:

| ID | Condition |
|---|---|
| `TXCL001` | Class has `[Inject]` members — DI is unavailable on standalone commands. |
| `TXCL002` | Class has a constructor with parameters — standalone commands require a parameterless constructor. |
| `TXCL003` | Class declares both `MainAsync` and `ExecuteAsync`/`Execute`. |

`[Argument]`, `[Option]`, and `[Options]` still bind as usual from `ParseResult` — the
only capability removed is constructor/member DI.

#### Cancellation

For now, `StandaloneHost` passes `CancellationToken.None` to `MainAsync`. Standalone
commands are expected to wire their own process-termination handling if needed (e.g.
via `Console.CancelKeyPress` or `PosixSignalRegistration`). Full cancellation flow
on par with `ToolHost` (which benefits from System.CommandLine's `ProcessTerminationTimeout`)
is a follow-up.

### Reusable `IHostBuilder` extensions

`UseSerilog` and `UseDefaultConfiguration` target `IHostBuilder` directly, so the same
configuration and logging bootstrap can be applied to any generic host — for example a
web host built for a specific subcommand:

```csharp
var builder = Tool.CreateBuilder(args).UseDefaults();
var parse = builder.Parse();
if (parse.CommandResult.Command.Name == "serve")
{
    var web = WebApplication.CreateBuilder(args);
    web.Host
        .UseDefaultConfiguration(environmentVariablePrefix: "MYTOOL_")
        .UseSerilog();
    var app = web.Build();
    app.MapGet("/", () => "hello");
    return await app.RunAsync().ContinueWith(_ => 0);
}
return await builder.RunAsync();
```

`UseSerilog` reads `ParseResult` from DI when present (to apply `--verbosity` / `-v` /
`-q` / `VerbosityOptions`). On alternate hosts where `ParseResult` is not registered,
the verbosity override is skipped and the minimum level falls back to whatever
`ReadFrom.Configuration` and your `configure` callback produced.

The CLI-specific extensions — `UseVerbosityOptions`, `UseObjectOutput`,
`AddCommandsFromAssembly`, `AddRecursiveOption` — remain `IToolBuilder`-only because
they mutate the `RootCommand` tree or the command-executor middleware chain, neither of
which exists on a generic host.

### Early access to `ParseResult`

`Parse()` runs System.CommandLine's parser against the current `RootCommand` tree and
caches the result. Calling it before `Build()` lets callers inspect the parsed command
line — for example to short-circuit into an entirely different host (a web host, a
worker host) when a specific subcommand is matched:

```csharp
var builder = Tool.CreateBuilder(args).UseDefaults();
var parse = builder.Parse();
if (parse.CommandResult.Command.Name == "serve")
{
    return await RunWebAsync(parse, args);
}
return await builder.RunAsync();
```

`Parse()` is idempotent — the returned `ParseResult` is cached, and `Build()` reuses
the cached instance. Once `Parse()` has been called, further modifications to
`RootCommand` are not reflected in the returned result.

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
class ToolHost(IServiceProvider services, ParseResult parseResult) : IHost, IHostApplicationLifetime
{
    public IServiceProvider Services => services;

    public CancellationToken ApplicationStarted { get; }
    public CancellationToken ApplicationStopping { get; }
    public CancellationToken ApplicationStopped { get; }
    public void StopApplication();

    public Task StartAsync(CancellationToken cancellationToken = default);
    public void Start();
    public Task StopAsync(CancellationToken cancellationToken = default);

    public int Invoke();            // parseResult.Invoke()
    public Task<int> InvokeAsync(); // parseResult.InvokeAsync()

    public void Dispose();          // disposes CTS instances and the ServiceProvider
}
```

`ToolHost` also implements `IHostApplicationLifetime`, registered in DI via a deferred
singleton factory. Consumers can inject it to observe lifecycle events or trigger graceful
shutdown (e.g. a `WindowsServiceBridge` calling `lifetime.StopApplication()` on SCM stop).

Lifecycle:

1. `Build()` constructs `ToolHost(provider, parseResult)`.
2. `StartAsync` enumerates `IHostedService` instances from DI and calls `StartAsync` on
   each in registration order, then fires `ApplicationStarted`.
3. `Invoke` / `InvokeAsync` runs the command via `ParseResult.Invoke`/`InvokeAsync`.
4. `StopAsync` fires `ApplicationStopping`, calls `StopAsync` on every hosted service in
   **reverse** order, then fires `ApplicationStopped`.
5. `Dispose` disposes the lifetime `CancellationTokenSource` instances and the
   `ServiceProvider`.

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
