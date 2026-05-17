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

1. Parses the command line via `RootCommand.Parse(args)` → `ParseResult`.
2. Creates a `HostBuilderContext` and stashes a build-time `InvocationContext` (containing
   only the `ParseResult`) in `HostBuilderContext.Properties`.
3. Runs every `ConfigureAppConfiguration` callback against the `HostBuilderContext`.
4. Runs every `ConfigureServices(HostBuilderContext, IServiceCollection)` callback.
5. Registers the library's own services:
   - `ParseResult` as a singleton
   - `IConfiguration` (backed by `IConfigurationManager`)
   - `ILoggerFactory` / `ILogger<T>` via `AddLogging()`
   - `IHostApplicationLifetime` (backed by `ToolHost`)
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

### Registering services with the generated entry point

If you rely on the [source-generated `Main`](source-generator.md#generated-entry-point)
there's no obvious place to call `ConfigureServices` — the whole point of the generated
entry point is that you don't write one. Mark any static method with
`[ConfigureServices]` and the generator will wire it into the chain for you:

```csharp
public static class Startup
{
    [ConfigureServices]
    public static void Register(IServiceCollection services)
    {
        services.AddHttpClient();
        services.AddSingleton<IMyService, MyService>();
    }
}
```

Requirements:

- Must be `static`.
- Must return `void`.
- Must take exactly one parameter of type `IServiceCollection`.
- Must be accessible from the generated code (`public` or `internal` — the generated
  entry point lives in the same assembly).

Methods that don't match are silently ignored so the attribute stays a no-op marker
rather than a compile-time guard. Multiple `[ConfigureServices]` methods across the
assembly are supported; the generator emits them in a stable order (ordinal by
declaring type's fully-qualified name, then by method name).

### Customizing the builder with `[Configure]`

`[ConfigureServices]` only hands you an `IServiceCollection` and always runs *on top of*
the opinionated default stack. When a startup hook needs to touch the builder or host
itself — add configuration sources, change logging, or replace the defaults — mark a
static method with `[Configure]` instead. It accepts **any combination** (each at most
once, in any order) of `IToolBuilder` / `IHostBuilder` / `IServiceCollection` — the same
signatures a [per-command `Configure`](#per-command-configure) method allows:

```csharp
public static class Startup
{
    [Configure]
    public static void Setup(IToolBuilder builder, IServiceCollection services)
    {
        builder.UseDefaultLogging();
        builder.UseDefaultConfiguration();
        services.AddSingleton<IMyService, MyService>();
    }
}
```

The requirements match `[ConfigureServices]` (static, `void`, accessible, non-matching
shapes ignored, stable ordinal order). The difference is ownership: because the hook can
fully configure the builder, **its presence makes the source-generated entry point skip
the opinionated logging and default-configuration helpers** (`UseSerilog`,
`UseVerbosityOptions`, `UseDefaultConfiguration`). A hook restores them with
`UseDefaultLogging()` — the combined `UseSerilog` + `UseVerbosityOptions` one-liner — and
`UseDefaultConfiguration()`, not `UseDefaults()`, which would register every command a
second time. Command discovery and `UseObjectOutput()` are still generated automatically
(the latter whenever a `[Command]` returns a value), so the hook never adds those. Keep
using `[ConfigureServices]` when you only need to register services and want the default
stack left in place.

### Per-command `Configure`

A `Configure` method on a `[Command]` type fires only when that command is actually
invoked, after parsing and before the service provider is built — so registrations
land in the host that runs the command. Two shapes are supported:

**Static `Configure`** is the simplest form. It runs without a command instance and
is the only option when the command needs constructor DI:

```csharp
[Command("greet")]
public class GreetCommand
{
    [Inject] private IGreeter _greeter = null!;

    public void Execute() => _greeter.Greet();

    public static void Configure(IServiceCollection services)
        => services.AddSingleton<IGreeter, ConsoleGreeter>();
}
```

**Instance `Configure`** runs on a constructed command with `[Argument]`/`[Option]`
values already bound, so it can register services keyed off parsed input. The
configure-phase instance is reused by `Execute`, so any state set in `Configure`
carries through:

```csharp
[Command("fetch")]
public class FetchCommand
{
    [Argument("url")] public string Url { get; set; } = "";
    [Inject] public IHttpClientFactory Http { get; set; } = null!;

    private Uri _parsed = null!;

    public void Configure(IServiceCollection services)
    {
        _parsed = new Uri(Url);
        services.AddHttpClient(_parsed.Host);
    }

    public Task ExecuteAsync() => Http.CreateClient(_parsed.Host).GetAsync(_parsed);
}
```

Either form returns `void` and may take any combination of `IToolBuilder`,
`IHostBuilder`, and `IServiceCollection` (or no parameters at all). A method named
`Configure` on a `[Command]` type whose signature doesn't match these rules is
reported as `TXCL006` rather than silently dropped — name collisions get a clear
"rename or fix the signature" error instead of a hook that quietly never fires.

The instance form is the more capable shape and wins when both are declared.
Because the command is constructed before the service provider exists, instance
`Configure` cannot coexist with **constructor parameters** (`TXCL004`) or with
**required `[Inject]` members** (`TXCL005`) — those are reported as compile-time
errors. Non-required `[Inject]` members are fine; they're populated after
`Configure` runs and are visible by the time `Execute` is called.

See the [source-generator docs](source-generator.md#per-command-configure) for
emission details.

`TryAddTransient` / `TryAddSingleton` in the library means you can replace any of the
defaults from `ConfigureServices`:

```csharp
.ConfigureServices(s =>
{
    s.AddSingleton<ICommandExecutor, MyAuditingExecutor>();
});
```

## Commands are built via `new T(...)`

Each generated command action emits `new T(...)` with constructor parameters resolved from
DI via `GetRequiredService<T>()`, so **constructor injection just works** with no additional
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

1. Command instance is constructed via `new T(...)` with DI-resolved constructor args.
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
