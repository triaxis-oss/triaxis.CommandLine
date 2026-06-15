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

### `ConfigureConfiguration`

`ConfigureConfiguration` is the configuration-side counterpart to `ConfigureServices`,
with the same two-overload shape. It keeps the fluent `IToolBuilder` chain intact, so you
don't have to cast to `IHostBuilder` (which would return `IHostBuilder`) or reach into the
raw `IConfigurationManager` to add sources:

```csharp
Tool.CreateBuilder(args)
    .ConfigureConfiguration(config =>
        config.AddJsonFile("appsettings.json", optional: true))
    .ConfigureConfiguration((ctx, config) =>
    {
        var env = ctx.GetInvocationContext().ParseResult.GetValue<string>("--environment");
        config.AddJsonFile($"appsettings.{env}.json", optional: true);
    })
    .AddCommandsFromAssembly()
    .Run();
```

| Overload | When it runs | Access to `HostBuilderContext`? |
| --- | --- | --- |
| `ConfigureConfiguration(Action<IConfigurationBuilder>)` | Immediately, against `builder.Configuration`. | No. |
| `ConfigureConfiguration(Action<HostBuilderContext, IConfigurationBuilder>)` | Deferred until `Build()`. | Yes. |

The immediate overload adds the source to `IToolBuilder.Configuration` right away, so it is
visible to any later configuration reads on the builder. The deferred overload runs during
`Build()` and can branch on the parsed command line via `ctx.GetInvocationContext()`. Both
overloads are replayed by `ApplyTo` (immediate sources via the builder's
`IConfigurationManager`, deferred ones via the `ConfigureAppConfiguration` callback list),
so the alternate-host story works unchanged.

### Scoped configuration & subtree remapping

`UseScopedConfiguration` groups sources into precedence layers
(`ConfigurationScope`, least → most specific: `Builtin`, `Machine`, `User`,
`EnvironmentVariables`, `Override`) and optionally **remaps a subtree** onto another
path. The key guarantee: a remapped subtree overlays *its own* scope's primary tree,
but a less specific scope's subtree **never** overrides a more specific scope's
primary value — so a built-in environment overlay can't clobber an explicit user or
override setting.

```csharp
Tool.CreateBuilder(args)
    .UseScopedConfiguration(cfg => cfg
        .Add(ConfigurationScope.Builtin, c => c.AddJsonFile("appsettings.json", optional: true))
        .Add(ConfigurationScope.User,    c => c.AddJsonFile(userPath, optional: true))
        .Add(ConfigurationScope.Override, c => c.AddJsonFile(explicitPath, optional: true))
        // overlay the selected environment section onto the root tree:
        .Remap("Environments:Production")
        // or move a subtree to an explicit target path:
        .Remap("Profiles:CI", "Logging"))
    .AddCommandsFromAssembly()
    .Run();
```

The merged result is exposed as a **single** `IConfigurationSource` whose internal
precedence is, per key (last wins): for each scope ascending in specificity, that
scope's *primary* tree, then that scope's *remapped subtree*. That ordering is what
makes `subtree(scope) < primary(moreSpecificScope)` hold while
`subtree(scope) > primary(sameScope)`. Being one source, it replays through `ApplyTo`
as a unit and its precedence is independent of where it sits in an outer builder.

`Remap(fromPath)` overlays the subtree onto the root; `Remap(fromPath, toPath)` moves
it under `toPath`. Remaps apply independently to every scope that has sources.

The opinionated `UseDefaultConfiguration` is built on this: `appsettings.json` →
`Builtin`, the all-users override file → `Machine`, the per-user override files →
`User`, environment variables → `EnvironmentVariables`. Its effective precedence is
unchanged (the machine probe is additive); pass its `configure` hook to add an
`Override` source or `Remap` rules.

#### Composable scope helpers

The probing `UseDefaultConfiguration` does is also exposed as `ScopedConfigurationBuilder`
extensions, so you can assemble the same shape yourself — typically from a `[Configure]`
hook — without re-deriving the folders or precedence:

```csharp
builder.UseScopedConfiguration(s => s
    .AddBuiltinConfiguration()                 // appsettings.json (or a custom name) from AppContext.BaseDirectory → Builtin
    .AddJsonOverrides("myapp/config.json")     // per-machine + per-user probes → Machine / User (writable)
    .AddEnvironmentOverrides("MYAPP_"));        // environment variables → EnvironmentVariables
```

`AddYamlOverrides` is the YAML twin of `AddJsonOverrides`; both register *writable*
providers (see [Persisting scope-targeted changes](#persisting-scope-targeted-changes)).

`AddOverrides(relativePath, addFile)` is the format-neutral engine: it registers the
file in `CommonApplicationData` (→ `Machine`), then `ApplicationData` and
`LocalApplicationData` (both → `User`), calling your `addFile(builder, directory,
fileName)` delegate once per folder. It hands you the folder root and relative name
separately so you can root a watching provider at the (existing) folder rather than
the file's possibly-absent parent. The file is registered **unconditionally, whether
or not it exists yet** — so `addFile` must add it as `optional` with reload-on-change;
otherwise a file written after start-up (the common case for user/machine overrides
in a long-running process) is never picked up because no watcher was attached.
`AddJsonOverrides` / `AddYamlOverrides` are the JSON/YAML-flavoured wrappers (built on
`AddPersistentJsonFile` / `AddPersistentYamlFile`). To plug in a different format, pass
your own `addFile` delegate:

```csharp
s.AddOverrides("myapp/config.toml", (c, dir, file) =>
    c.AddTomlFile(new PhysicalFileProvider(dir), file, optional: true, reloadOnChange: true));
```

The override path keeps its extension, so you choose `.json` / `.yaml` / `.yml`
freely. The override and builtin helpers default to `reloadOnChange: true` — the
scoped source folds reloads back in and propagates them, so an override file edited
*or created* after start-up is picked up live. `UseDefaultConfiguration` is unchanged
and is not built on these; they are an additive surface for hand-composed pipelines.

#### Persisting scope-targeted changes

`IConfiguration.Update(scope, cp => …)` mutates and persists the writable source
registered for one `ConfigurationScope`, e.g. write the user override without
touching machine or builtin:

```csharp
configuration.Update(ConfigurationScope.User, cp => cp.Set("MyTool:Token", token));
```

The mutation runs against that scope's `IPersistentConfigurationProvider`; `Save()`
is called once the callback returns and raises the provider's reload token, so the
live `IConfiguration` reflects the write immediately. Scope targeting is
deterministic — it writes exactly the requested layer rather than guessing from
source position — and the scoped source is located even when a host nests it behind
a chained provider.

The base package ships only the contract: `IPersistentConfigurationProvider` is
`IConfigurationProvider` plus `Save` (`Set` is already on `IConfigurationProvider`, so
a `ConfigurationProvider`-derived writer adds only `Save()`). The **Tool** package
ships the concrete writers — `AddPersistentJsonFile` / `AddPersistentYamlFile` (and
the `AddJsonOverrides` / `AddYamlOverrides` helpers that wrap them) register a
writable file provider, so `Update` works out of the box without a custom writer.
They read exactly like `AddJsonFile` (optional, reload-on-change). `Save()` applies
the **minimal edit**: it patches only the keys changed since the last save, so
comments, whitespace, key order, and untouched values are kept byte-for-byte. New
keys are inserted (creating missing parents) and a JSON array that gains a
non-positional key is rewritten in place to an object; a brand-new file, or a
document the editor will not touch (flow-style YAML, anchors, a non-object root),
gets a fresh canonical document instead. `Update` throws
`InvalidOperationException` when no scope-layered source is present, or when the
target scope has no writable source — a scope-targeted write requires
`UseScopedConfiguration` / `UseDefaultConfiguration`.

### Standalone commands (`Main` / `MainAsync`)

A command can opt out of the CLI-side service provider entirely by declaring `Main`
(sync) or `MainAsync` (async) instead of `ExecuteAsync`/`Execute`. When the matched
command is standalone, `Build()` short-circuits before constructing a service provider —
the CLI host is never built, no middleware runs, and the command takes full
responsibility for its own lifecycle.

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

`Main` / `MainAsync` may take any combination of `IToolBuilder` and `CancellationToken`
(in that order). `MainAsync` returns `Task` or `Task<int>`; `Main` returns `void` or
`int` (an `Async` suffix on a void-returning method would be a contradiction):

```csharp
public Task MainAsync();
public Task MainAsync(CancellationToken ct);
public Task MainAsync(IToolBuilder builder);
public Task MainAsync(IToolBuilder builder, CancellationToken ct);
public Task<int> MainAsync( …same four shapes… );

public void Main();
public void Main(CancellationToken ct);
public void Main(IToolBuilder builder);
public void Main(IToolBuilder builder, CancellationToken ct);
public int  Main( …same four shapes… );
```

`void` / `Task` returns yield exit code `0`; `int` / `Task<int>` returns the value.
`MainAsync` wins over `Main` when both are declared on the same type.

#### `IToolBuilder.ApplyTo(IHostBuilder)`

`ApplyTo` replays the builder's configuration sources, service registrations, and
deferred `IHostBuilder` callbacks onto any `IHostBuilder` — typically the `Host`
property of a `WebApplicationBuilder`.

The replay is **isolated**. The tool's contribution is materialised first, against a
scratch `HostBuilderContext` with the tool's own `Properties`:

1. A fresh `ConfigurationBuilder` is seeded with the tool's direct configuration
   sources, then handed to every deferred `ConfigureAppConfiguration` callback
   (e.g. from `UseDefaultConfiguration`). Everything produced is `Build()`-ed into a
   standalone `IConfigurationRoot`.
2. A fresh `IServiceCollection` is seeded with the tool's direct service descriptors
   and the current `ParseResult` as a singleton, then handed to every deferred
   `ConfigureServices(HostBuilderContext, IServiceCollection)` callback (e.g. the
   Serilog factory from `UseSerilog`). During this step the scratch
   `HostBuilderContext.Configuration` is the built tool configuration from step 1 —
   matching what `ToolBuilder.Build()` exposes to the same callbacks.

The scratch configuration is then attached to the target via a single
`ConfigureAppConfiguration` callback (`cfg.AddConfiguration(toolConfiguration)`),
and the scratch services are added as a single bulk `ConfigureServices` callback.

Because the tool's deferred delegates run against a scratch builder, destructive
operations like `cfg.Sources.Clear()` inside a `UseXxx` extension only see the
tool's own sources and cannot reach the target's — including user-added sources
like a `--config` YAML or anything the caller appended to
`WebApplicationBuilder.Configuration`. The target controls precedence by ordering
its own registrations relative to the `ApplyTo` call: anything added before
`ApplyTo` is overridden by the tool's layer; anything added after overrides it.

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
| `TXCL003` | Class declares both `Main` / `MainAsync` and `ExecuteAsync`/`Execute`. |

`[Argument]`, `[Option]`, and `[Options]` still bind as usual from `ParseResult` — the
only capability removed is constructor/member DI.

#### Cancellation

For now, `StandaloneHost` passes `CancellationToken.None` to `Main`/`MainAsync`. Standalone
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
    var host = builder.Build();
    await using var hostDisposal = host.AsAsyncDisposable();
    await host.StartAsync();
    try { return await ((ToolHost)host).InvokeAsync(); }
    finally { await host.StopAsync(); }
}
```

`AsAsyncDisposable()` is an internal extension that returns the target itself when it
already implements `IAsyncDisposable`, otherwise a tiny adapter that forwards
`DisposeAsync` to `IDisposable.Dispose`. `RunAsync` uses it to tear down containers
holding `IAsyncDisposable`-only services without hitting `ServiceProvider.Dispose()`'s
sync-only check. Sync `Dispose` bridges to the same async path
(`DisposeAsync().AsTask().GetAwaiter().GetResult()`), so async-only disposables shut
down cleanly under `Run` too — it just blocks on the teardown instead of awaiting it.

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

    public void Dispose();              // bridges to DisposeAsync().GetAwaiter().GetResult()
    public ValueTask DisposeAsync();    // disposes CTS instances + awaits the ServiceProvider
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
5. `Dispose` / `DisposeAsync` disposes the lifetime `CancellationTokenSource` instances
   and the `ServiceProvider`. `RunAsync` awaits `DisposeAsync` directly; sync `Run` uses
   `Dispose`, which bridges to `DisposeAsync` (blocking on the result) so async-only
   singletons are released either way.

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
