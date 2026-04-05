# Dependency injection and `[Inject]`

`triaxis.CommandLine` embeds a standard
`Microsoft.Extensions.DependencyInjection.ServiceProvider` into the command pipeline.
The executor is a service, the logger factory is a service — and the bridge between
"parsed command line" and "services that can read parsed values" is `ParseResult` itself,
registered as a singleton during build.

`IToolBuilder` extends `IHostBuilder`, so everything you know about
`Microsoft.Extensions.Hosting` applies — see [hosting.md](hosting.md) for the hosting
angle. This document focuses on the DI details.

## How the provider is assembled

`IHostBuilder.Build()` runs inside `Run`/`RunAsync`. It:

1. Realizes the command tree and parses the command line (`ParseResult`).
2. Creates a `HostBuilderContext` and stashes a build-time `InvocationContext` (containing
   only the `ParseResult`) in `HostBuilderContext.Properties`.
3. Runs every `ConfigureAppConfiguration` callback against the `HostBuilderContext`.
4. Runs every `ConfigureServices(HostBuilderContext, IServiceCollection)` callback.
5. Registers the library's own services:
   - `ParseResult` as a singleton
   - `IConfiguration` (backed by `IConfigurationManager`)
   - `ILoggerFactory` / `ILogger<T>` via `AddLogging()`
   - `ICommandExecutor` (the middleware-aware executor)
6. Builds the provider and wraps it in a `ToolHost`.

So every CLI tool — regardless of which `Use*` extensions it opts into — has, at a minimum,
`ParseResult`, `IConfiguration`, logging, and `ICommandExecutor` available in DI. Your own
registrations stack on top via `ConfigureServices`:

```csharp
Tool.CreateBuilder(args)
    .UseDefaults()
    .ConfigureServices(services =>
    {
        services.AddHttpClient();
        services.AddSingleton<IMyService, MyService>();
    })
    .Run();
```

Or with access to the `HostBuilderContext` (standard `IHostBuilder` overload):

```csharp
((IHostBuilder)builder).ConfigureServices((ctx, services) =>
{
    services.Configure<MyOptions>(ctx.Configuration.GetSection("My"));
});
```

`ConfigureServices` is composable — call it multiple times. The triaxis-specific overload
(`Action<IServiceCollection>`) executes immediately against the underlying
`IServiceCollection`; the `IHostBuilder` overload (`Action<HostBuilderContext, IServiceCollection>`)
defers until `Build()`.

`TryAddTransient` / `TryAddSingleton` in the library means you can replace any of the
defaults from `ConfigureServices`:

```csharp
.ConfigureServices(s =>
{
    s.AddSingleton<ICommandExecutor, MyAuditingExecutor>();
});
```

## Commands are built via `ActivatorUtilities`

Each generated command action emits `ActivatorUtilities.CreateInstance<T>(provider)` to
build its command instance, so **constructor injection just works** with no additional
ceremony and no need to register the command type in DI:

```csharp
[Command("fetch")]
public class FetchCommand(IHttpClientFactory http, ILogger<FetchCommand> logger)
{
    public async Task ExecuteAsync(CancellationToken ct) { /* use http, logger */ }
}
```

> **Why both constructor injection and `[Inject]`?** Constructor injection is the
> recommended DI pattern and works out of the box. `[Inject]` exists so that reusable
> **base command classes** can pull in their own dependencies without every derived
> command having to declare and forward them through its constructor. `LoggingCommand`
> in `triaxis.CommandLine.Tool` is the canonical example: it has a protected
> `[Inject] ILoggerFactory _loggerFactory` field and exposes a `Logger` property, so any
> command that derives from it gets logging for free — no constructor plumbing required.
> The two mechanisms compose: a derived command can still take its own dependencies via
> a constructor.

## The `[Inject]` attribute

```csharp
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class InjectAttribute : Attribute
{
    public InjectAttribute() { }
    public InjectAttribute(Type type) { Type = type; }

    public Type? Type { get; set; }
}
```

Usage:

```csharp
[Command("diag")]
public class DiagCommand
{
    [Inject] private readonly ILogger<DiagCommand> _log = null!;
    [Inject] private readonly IConfiguration _config = null!;
    [Inject(typeof(IMyService))] private readonly IMyService _svc = null!;

    public void Execute()
    {
        _log.LogInformation("Using {Service}", _svc.GetType().Name);
    }
}
```

Two things to note:

1. **Works on fields and properties**, public or private.
2. **Explicit type override** — `[Inject(typeof(TService))]` asks DI for a specific service
   type rather than the member's declared type. Useful when the field type is a base class
   or interface and you want a concrete implementation from DI.

## How `[Inject]` is resolved at runtime

The generator inlines `[Inject]` assignments into each command action. For each annotated
member it emits either:

```csharp
instance.Property = provider.GetRequiredService<TService>();
```

for public settable members, or an accessor-wrapped variant for non-public / read-only
members:

```csharp
__access__log._Set(instance, provider.GetRequiredService<ILogger<DiagCommand>>());
```

Injection order inside the generated `InvokeAsync`:

1. Command instance is resolved via `ActivatorUtilities.CreateInstance<T>(provider)`
   (constructor injection happens here).
2. `[Inject]` members are populated.
3. `[Options]` nested instances are created if null.
4. Arguments and options are bound (see [parameter-binding.md](parameter-binding.md)).
5. `Execute` / `ExecuteAsync` is invoked.

## `ParseResult` as a service

`ParseResult` is registered as a singleton during `Build()`. Any service can inject it to
read global options, discover which subcommand ran, or re-bind values. `UseSerilog`,
`UseObjectOutput`, and `VerbosityOptions.GetEffectiveLevel` all rely on this.

```csharp
public class MyHealthCheck(ParseResult parseResult, ILogger<MyHealthCheck> log)
{
    public void Check()
    {
        var verbosity = VerbosityOptions.GetEffectiveLevel(parseResult);
        log.LogDebug("Verbosity is {Verbosity}", verbosity);
    }
}
```

At configuration time — inside `ConfigureAppConfiguration` / `ConfigureServices(HostBuilderContext, ...)`
callbacks — the provider doesn't exist yet, but you can still reach `ParseResult` via
`context.GetInvocationContext().ParseResult`. See [hosting.md](hosting.md).

## Replacing the defaults

Because every core service is registered with `TryAdd*`, you can replace any of them from
`ConfigureServices`. The ones most likely to be overridden:

| Service | Purpose | Typical reason to replace |
| --- | --- | --- |
| `ICommandExecutor` | Runs middleware + finalization. | Add telemetry, custom error policy. |
| `IObjectOutputHandler<T>` | Formats a specific result type. | Custom rendering for your domain types. |
| `IObjectFormatterProvider` | Picks a formatter based on `--output`. | Add a new format. |
| `IObjectDescriptorProvider<T>` | Builds descriptors for a type. | Customize column order, visibility, titles. |

See [middleware.md](middleware.md) and [object-output.md](object-output.md) for the details
of those pipelines.
