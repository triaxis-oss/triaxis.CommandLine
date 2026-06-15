# triaxis.CommandLine

An opinionated extension on top of [System.CommandLine](https://learn.microsoft.com/dotnet/standard/commandline/)
for quickly bootstrapping modern .NET command line tools. It adds:

- Attribute-based automatic command discovery via a source generator
- Dependency injection via `Microsoft.Extensions.DependencyInjection`
- Configuration via `Microsoft.Extensions.Configuration` (`appsettings.json`, env vars, overrides)
- Structured logging via [Serilog](https://serilog.net/) with `-v` / `-q` / `--verbosity` flags
- Object output formatting (`Table` / `Wide` / `Json` / `Yaml` / `Raw`) via a single `--output` flag
- A middleware pipeline around command execution
- Cooperative cancellation on Ctrl+C / SIGTERM
- Standalone `Main` / `MainAsync` commands that own their own host (e.g. ASP.NET Core inside
  a subcommand)

## Packages

| Package | Purpose |
| --- | --- |
| `triaxis.CommandLine` | Core `ToolBuilder`, attributes, command discovery, DI |
| `triaxis.CommandLine.ObjectOutput` | `--output` formatters (Table/Wide/Json/Yaml/Raw/None) |
| `triaxis.CommandLine.Serilog` | Serilog integration and `--verbosity` / `-v` / `-q` options |
| `triaxis.CommandLine.Tool` | Opinionated all-in-one meta-package (`UseDefaults()`) |

The core libraries target `netstandard2.0` and `netstandard2.1`, so they can be consumed from any
modern .NET or .NET Framework host. Tools built on top typically target `net8.0` or newer.

## Quick start

Install the meta-package into a console project:

```shell
dotnet new console -n MyTool
cd MyTool
dotnet add package triaxis.CommandLine.Tool
```

Replace `Program.cs` with a one-liner:

```csharp
return Tool.CreateBuilder(args).UseDefaults().Run();
```

Or **delete `Program.cs` entirely**: if the project is an executable and has no user-written
entry point, the source generator synthesizes one that is equivalent to the line above. See
[Source-generated entry point](#source-generated-entry-point) below.

Add a command class anywhere in the assembly:

```csharp
[Command("hello", Description = "Greets the world, or someone")]
public class HelloCommand : LoggingCommand
{
    [Option("--name", "-n", Description = "Name of the person to greet")]
    public string Name { get; set; } = "World";

    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        Logger.LogDebug("Greeting {Name}...", Name);
        Console.WriteLine($"Hello {Name}!");
        return Task.CompletedTask;
    }
}
```

Run it:

```shell
dotnet run -- hello
dotnet run -- hello --name Alice
dotnet run -- hello -n Alice -v           # -v raises log level to Debug
dotnet run -- hello --help                # System.CommandLine generated help
```

`UseDefaults()` composes `UseSerilog()`, `UseVerbosityOptions()`, `UseObjectOutput()`,
`UseDefaultConfiguration()` and `AddCommandsFromAssembly()` — see [The `Tool`
meta-package](#the-tool-meta-package) below. The source-generated entry point chains the
individual helpers directly instead of calling `UseDefaults`, and omits `UseObjectOutput`
(along with the YamlDotNet dependency) when no command produces output, so the formatter
stack can be trimmed.

## Building blocks

### `ToolBuilder`

`Tool.CreateBuilder(args)` returns an `IToolBuilder`:

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

Because `IToolBuilder` extends `IHostBuilder`, you can use standard hosting APIs
(`ConfigureAppConfiguration`, `ConfigureServices(HostBuilderContext, …)`, `Properties`,
`IHostedService`) on the builder without casting.

`Run` and `RunAsync` are extension methods that call `IHostBuilder.Build()` to produce a
`ToolHost`, start hosted services, delegate to `ParseResult.Invoke` / `InvokeAsync`, stop
hosted services, and dispose the service provider. System.CommandLine owns the actual
parse, help, ctrl-c and error handling. See [docs/hosting.md](docs/hosting.md) for the full
lifecycle.

A fully manual setup without `UseDefaults` looks like:

```csharp
return await Tool.CreateBuilder(args)
    .ConfigureServices(s => s.AddSingleton<IMyService, MyService>())
    .UseSerilog()
    .UseVerbosityOptions()
    .UseObjectOutput()
    .AddCommandsFromAssembly()
    .RunAsync();
```

### Commands

A command is any class annotated with `[Command]` that exposes a public `Execute` or
`ExecuteAsync` method. Command instances are built per invocation via
`ActivatorUtilities.CreateInstance<T>(provider)`, so constructor injection works out of
the box — the class does not have to be pre-registered.

```csharp
[Command("db", "migrate", Description = "Apply pending migrations")]
public class MigrateCommand
{
    public Task<int> ExecuteAsync(CancellationToken cancellationToken) { /* ... */ }
}
```

- The path can have one or more segments — nested segments become subcommands
  (`mytool db migrate`).
- `[Command]` is `AllowMultiple = true`, so you can put several of them on the same class
  to expose it under multiple paths. It can **also** be applied at the **assembly** level
  to attach a description or aliases to an intermediate tree node that has no dedicated
  class (e.g. `[assembly: Command("db", Description = "Database operations")]`).
- Supported return types: `void`, `int`, `Task`, `Task<int>`, `ICommandInvocationResult`,
  `Task<ICommandInvocationResult>`, and — when `UseObjectOutput` is enabled — any `T`,
  `IEnumerable<T>`, `IAsyncEnumerable<T>`, `Task<T>`, `Task<IEnumerable<T>>`, and
  `System.Data.DataTable`.
- `CommandAttribute` properties: `Path`, `Aliases`, `Description`.
- `[SupportedOSPlatform("windows"|"linux"|"macos"|...)]` on a command class (or on a
  base class) gates its registration: the command only appears when the current OS
  matches one of the listed platforms. Multiple attributes combine with a logical OR.

Commands are discovered via `AddCommandsFromAssembly()`. Discovery is entirely
source-generated — the `triaxis.CommandLine` package ships a Roslyn source generator under
`analyzers/` that emits one `AsynchronousCommandLineAction` subclass per command plus a
`[ModuleInitializer]` that registers them all. `AddCommandsFromAssembly` throws if no
generated registration is present, which in practice only happens if the assembly was
compiled without a reference to the package.

#### Standalone commands (`Main` / `MainAsync`)

A `[Command]` class can declare a `Main` (sync) or `MainAsync` (async) method instead
of `Execute`/`ExecuteAsync`.
The generator emits an action that skips the DI container and middleware pipeline
entirely and passes the `IToolBuilder` straight through, letting the command stand up
its own host. `IToolBuilder.ApplyTo(IHostBuilder)` replays the tool's configuration
sources and service registrations onto the alternate host. The tool's own deferred
delegates (from `UseSerilog`, `UseDefaultConfiguration`, etc.) run in isolation against
a scratch builder, so destructive operations inside those extensions cannot reach
target-owned state. `ApplyTo` also seeds the build-time `InvocationContext` into the
target's `IHostBuilder.Properties`, so `ctx.GetInvocationContext()` works for any
deferred callback registered on the target side. The target controls precedence:
anything registered before `ApplyTo` is overridden by the tool's layer; anything
registered after overrides it.

```csharp
[Command("serve", Description = "Runs the greeter as an HTTP server.")]
public class ServeCommand
{
    [Option("--port")] public int Port { get; set; } = 5000;

    public async Task<int> MainAsync(IToolBuilder builder, CancellationToken ct)
    {
        var web = WebApplication.CreateBuilder();
        web.Logging.ClearProviders();     // drop ASP.NET Core's defaults
        builder.ApplyTo(web.Host);        // replay CLI-side config / services / Serilog
        web.WebHost.UseUrls($"http://localhost:{Port}");

        var app = web.Build();
        app.MapGet("/", (IGreeter g) => g.Greet("World"));
        await app.RunAsync(ct);
        return 0;
    }
}
```

Recognized signatures are `Main([IToolBuilder,] [CancellationToken])` returning
`void` or `int`, and `MainAsync([IToolBuilder,] [CancellationToken])` returning `Task`
or `Task<int>`. Declaring a `CancellationToken` opts the command into System.CommandLine's
process-termination handling: the `ct` above is wired to Ctrl+C / SIGTERM (the same token a
`ToolHost` command gets), so `await app.RunAsync(ct)` shuts the host down cooperatively.
Omit it and the command is invoked directly, with no framework cancellation machinery — it
owns its lifecycle outright. Standalone commands can still use
`[Argument]`/`[Option]`/`[Options]` binding, but cannot mix with `[Inject]` members or
constructor DI — their whole point is that no service provider is constructed on the CLI
side. See [`examples/WebHost`](./examples/WebHost) for a full walkthrough.

### Arguments and options

Bind parsed values to **fields or properties** using `[Argument]` (positional) or `[Option]`
(named). Both derive from `CommandlineAttribute`, which exposes common metadata:

| Property | Meaning |
| --- | --- |
| `Name` | Explicit name. Defaults to the member name converted to kebab-case (e.g. `MyOption` → `--my-option`, `MyArg` → `MY-ARG`). |
| `Description` | Shown in `--help`. |
| `Order` | Sort order for help and positional ordering. |

`ArgumentAttribute` adds `Required`. `OptionAttribute` adds `Aliases` and `Required`. The
`required` C# keyword on the member is also honoured automatically.

```csharp
[Command("copy")]
public class CopyCommand
{
    [Argument(Description = "Source path", Required = true)]
    public string Source { get; set; } = null!;

    [Argument(Description = "Destination path", Required = true)]
    public string Destination { get; set; } = null!;

    [Option("--force", "-f", Description = "Overwrite existing files")]
    public bool Force { get; set; }

    [Option("--retries", Description = "Number of retries on transient errors")]
    public int Retries { get; set; } = 3;

    public void Execute() { /* ... */ }
}
```

Run as:

```shell
mytool copy ./a.txt ./b.txt --force --retries 5
```

#### Grouping options

Use `[Options]` on a property whose type holds further `[Option]`/`[Argument]` members to
flatten a nested object into the command without writing the members inline:

```csharp
public class NetworkOptions
{
    [Option("--host")] public string Host { get; set; } = "localhost";
    [Option("--port")] public int Port { get; set; } = 443;
}

[Command("ping")]
public class PingCommand
{
    [Options] public NetworkOptions Network { get; set; } = new();
    public int Execute() { /* ... */ return 0; }
}
```

#### Alternate entry points (`[ActionOption]`)

A command can have additional entry points triggered by their own flag. Mark a method
with `[ActionOption]` and the generator exposes a boolean option that — when set —
runs that method instead of the command's primary `ExecuteAsync`/`MainAsync`. The same
arguments and options are still bound onto the command instance, so the alternate
method observes the same state the primary would have:

```csharp
[Command("backup")]
public class BackupCommand
{
    [Option("--target")] public string Target { get; set; } = "/var/backup";

    public Task ExecuteAsync(CancellationToken ct) { /* default: take a backup */ }

    [ActionOption("--list", "-l", Description = "List existing backups")]
    public Task ListAsync(CancellationToken ct) { /* ... */ }

    [ActionOption("--restore")]
    public Task<int> RestoreAsync(CancellationToken ct) { /* ... */ }
}
```

`backup` runs the primary; `backup --list` runs `ListAsync`; `backup --restore`
runs `RestoreAsync`. On standalone commands the alternate method may also take an
`IToolBuilder`, just like `MainAsync`.

### Dependency injection

Register services with `ConfigureServices`:

```csharp
Tool.CreateBuilder(args)
    .UseDefaults()
    .ConfigureServices((ctx, services) =>
    {
        services.AddHttpClient();
        services.AddSingleton<IMyService, MyService>();
        services.Configure<MyOptions>(ctx.Configuration.GetSection("My"));
    })
    .Run();
```

When you rely on the source-generated entry point (no hand-written `Main`), mark any
static method with `[ConfigureServices]` and the generator folds it into the chain
for you:

```csharp
public static class Startup
{
    [ConfigureServices]
    public static void Register(IServiceCollection services)
        => services.AddSingleton<IMyService, MyService>();
}
```

The method must be `static`, return `void`, and take a single `IServiceCollection`
parameter. Multiple hooks across the assembly are supported; the generator emits
them in a stable ordinal order (by declaring type's fully-qualified name, then by
method name).

When a hook needs to customize the builder or host itself (add configuration sources,
swap logging, replace the defaults), use `[Configure]` instead. It accepts any
combination of `IToolBuilder` / `IHostBuilder` / `IServiceCollection`:

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

Because a `[Configure]` hook owns builder setup, its presence makes the generated entry
point **skip the opinionated logging and default-configuration helpers** (`UseSerilog`,
`UseVerbosityOptions`, `UseDefaultConfiguration`) — restore them with `UseDefaultLogging()`
(the combined `UseSerilog` + `UseVerbosityOptions` one-liner) and `UseDefaultConfiguration()`
as shown, not `UseDefaults()`, which would re-register every command. Command discovery
and `UseObjectOutput()` are still generated automatically (the latter whenever a `[Command]`
returns a value), so a hook never needs to add those. `[ConfigureServices]` is unaffected
and stays the right choice when you only register services.

For any pre-container setup specific to a command — registering services, adding
middleware, tweaking the host — declare a static `Configure` method on the command
type. The generator wires it onto the command's action so it fires only when that
command is actually invoked:

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

The method must be `static` and return `void`. It can take any of `IToolBuilder`,
`IHostBuilder`, or `IServiceCollection` — including no parameters at all.

Inside a command, take services through the constructor as usual, or use the `[Inject]`
attribute on any field or property. `[Inject]` is particularly handy on reusable base
classes (see `LoggingCommand`) so derived commands don't have to forward dependencies
through their own constructors:

```csharp
[Command("fetch")]
public class FetchCommand
{
    [Inject] private readonly IHttpClientFactory _http = null!;
    [Inject] private readonly ILogger<FetchCommand> _logger = null!;
    [Inject] private readonly IOptions<MyOptions> _options = null!;

    public async Task ExecuteAsync(CancellationToken ct) { /* ... */ }
}
```

An `ExecuteAsync(CancellationToken)` overload receives the Ctrl+C token supplied by
System.CommandLine; the `Execute(CancellationToken)` signature is not recognized.

### Configuration

`IToolBuilder.Configuration` exposes an `IConfigurationManager` that is also registered into DI
as `IConfiguration`. `UseDefaults()` — and its extracted `UseDefaultConfiguration()` helper
— wire it up with:

- `appsettings.json` next to the executable (optional)
- An optional override file under `ApplicationData` / `LocalApplicationData`
- Optional environment-variable prefix

```csharp
Tool.CreateBuilder(args)
    .UseDefaults(
        configOverridePath: "MyTool/appsettings.json",
        environmentVariablePrefix: "MYTOOL_")
    .Run();
```

Or, when you want finer control (e.g. to skip `UseObjectOutput` so YamlDotNet can be
trimmed), call the helpers directly — this is what the source-generated `Main` does:

```csharp
Tool.CreateBuilder(args)
    .UseSerilog()
    .UseVerbosityOptions()
    .UseDefaultConfiguration(environmentVariablePrefix: "MYTOOL_")
    .AddCommandsFromAssembly()
    .Run();
```

To add your own configuration sources fluently, use `ConfigureConfiguration` — the
configuration-side counterpart to `ConfigureServices`, so you don't have to cast to
`IHostBuilder` or reach into the raw `IConfigurationManager`:

```csharp
Tool.CreateBuilder(args)
    .ConfigureConfiguration(c => c.AddJsonFile("custom.json", optional: true))
    .ConfigureConfiguration((ctx, c) =>
    {
        var env = ctx.GetInvocationContext().ParseResult.GetValue<string>("--environment");
        c.AddJsonFile($"appsettings.{env}.json", optional: true);
    })
    .Run();
```

The single-argument overload runs immediately against `IToolBuilder.Configuration`; the
two-argument overload is deferred until `Build()` and can branch on the parsed command
line. See [Hosting integration](docs/hosting.md) for details.

For environment-style overlays, `UseScopedConfiguration` groups sources into precedence
scopes (`Builtin` < `Machine` < `User` < `EnvironmentVariables` < `Override`) and can
remap a subtree onto the root (or any path). A less specific scope's overlay never
overrides a more specific scope's explicit value:

```csharp
Tool.CreateBuilder(args)
    .UseScopedConfiguration(cfg => cfg
        .Add(ConfigurationScope.Builtin, c => c.AddJsonFile("appsettings.json", optional: true))
        .Add(ConfigurationScope.User,    c => c.AddJsonFile(userPath, optional: true))
        .Remap("Environments:Production"))
    .Run();
```

`UseDefaultConfiguration` is built on this and accepts a `configure` hook for adding an
`Override` source or `Remap` rules. The same machine/user probing is also exposed as
composable `ScopedConfigurationBuilder` helpers — `AddBuiltinConfiguration`,
`AddJsonOverrides`, `AddEnvironmentOverrides`, and the format-neutral `AddOverrides`
engine (bring your own provider, e.g. YAML) — for hand-composed pipelines. See
[Hosting integration](docs/hosting.md#scoped-configuration--subtree-remapping).

Bind typed options in the usual way:

```csharp
.ConfigureServices((ctx, s) => s.Configure<MyOptions>(
    ctx.Configuration.GetSection("MyOptions")));
```

### Logging and verbosity

`UseSerilog()` registers an `ILoggerProvider` that creates a Serilog logger **lazily** after the
command line has been parsed. That means:

- `Serilog` section in `appsettings.json` is honoured (via `ReadFrom.Configuration`).
- The minimum level is derived from `--verbosity` / `-v` / `-q` at startup with no
  `LoggingLevelSwitch` needed.
- The console sink detects `FORCE_COLOR` and terminal themes automatically.

Verbosity flags added by `UseVerbosityOptions()` (and therefore `UseDefaults()`):

| Flag | Effect |
| --- | --- |
| `--verbosity <Trace\|Debug\|Information\|Warning\|Error\|Critical>` | Set explicitly |
| `-v`, `-vv` | Step up (`Debug`, then `Trace`) |
| `-q`, `-qq` | Step down (`Warning`, then `Error`) |

`appsettings.json` example:

```json
{
  "Serilog": {
    "MinimumLevel": "Information",
    "WriteTo": [{ "Name": "Console" }]
  }
}
```

An optional `LoggingCommand` base class (in `triaxis.CommandLine.Tool`) provides a preconfigured
`Logger` property and `CreateLogger(name)` helper.

### Object output

`UseObjectOutput()` (included in `UseDefaults()`) adds a recursive `--output` / `-o` option to
the root command and a middleware that formats whatever the command returns.

```csharp
public record Forecast(string City, decimal Temperature)
{
    [ObjectOutput(ObjectFieldVisibility.Extended)]
    public decimal TemperatureF => Temperature * 9 / 5 + 32;
}

[Command("forecast")]
public class ForecastCommand
{
    public IEnumerable<Forecast> Execute() =>
    [
        new("Bratislava", 21.5m),
        new("Prague",     19.0m),
        new("Paris",      23.2m),
    ];
}
```

```shell
mytool forecast                    # default table
mytool forecast -o Wide            # includes [Extended] fields
mytool forecast -o Json
mytool forecast -o Yaml
mytool forecast -o Raw             # ToString() per element
mytool forecast -o None            # discard output
```

Supported return shapes:

- `T`, `T[]`, `List<T>`, `IEnumerable<T>`, `IList<T>`
- `IAsyncEnumerable<T>` (streams row-by-row)
- `Task<T>`, `Task<IEnumerable<T>>`
- `ValueTuple<A, B, ...>` — combines fields from each element side-by-side
- `System.Data.DataTable` (sync or `Task<DataTable>`)

Use `[ObjectOutput]` to control field visibility (`Standard` / `Extended` / `Internal`) and to
position computed/extension fields with `Before = nameof(...)` / `After = nameof(...)`.

For commands that emit multiple result sets, inject `IObjectOutputHandler` and call it directly:

```csharp
[Command("watch")]
public class WatchCommand
{
    [Inject] private readonly IObjectOutputHandler _output = null!;

    public async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _output.ProcessOutputAsync(GetForecasts().ToCommandInvocationResult(), ct);
            await Task.Delay(1000, ct);
        }
    }
}
```

### Middleware

`AddMiddleware` wraps every command invocation in a chain. The first registered middleware is
the outermost. Object output, for example, is implemented as a middleware that runs after
`next()` to format the command's return value.

```csharp
builder.AddMiddleware(async (context, next) =>
{
    var sw = Stopwatch.StartNew();
    try
    {
        await next(context);
    }
    finally
    {
        context.Services.GetRequiredService<ILogger<Program>>()
            .LogInformation("Command {Command} finished in {Elapsed}",
                context.CommandType.Name, sw.Elapsed);
    }
});
```

`InvocationContext` exposes `Services`, `ParseResult`, `CommandType`, `InvocationResult`,
`ExitCode` and `GetCancellationToken()`.

### Error handling

Throw `CommandErrorException` from a command to report a user-facing failure. The default
executor logs it and exits without a stack trace. The exit code defaults to `-1` and is
configurable per throw via the `ExitCode` initializer:

```csharp
throw new CommandErrorException("File {Path} was not found", path);
throw new CommandErrorException("Config {Path} invalid", path) { ExitCode = 78 };
```

Any other exception bubbles up to System.CommandLine's default handler, which prints the
exception and returns a non-zero exit code — unless you register an exception mapper to
give it the same clean treatment:

```csharp
builder.MapException<TimeoutException>(exitCode: 124);
builder.MapException<HttpRequestException>(
    ex => new CommandError(75, "Upstream call failed: {Reason}", ex.Message));
```

Mappers run in registration order; the built-in `CommandErrorException` handling stays as
a final fallback. You can also replace the executor entirely by registering your own
`ICommandExecutor` in `ConfigureServices`.

### Cancellation

Commands declared as `ExecuteAsync(CancellationToken)` receive the token that
System.CommandLine raises on Ctrl+C / SIGTERM and get cooperative shutdown — including a
configurable termination timeout. Commands that do not accept a token get a
`Environment.FailFast(null)` callback registered on the token instead, so pressing Ctrl+C
during a non-cancellable command terminates the process immediately. The registration is
disposed as soon as the command body returns, so it never fires during middleware or
result finalization.

This applies to the `ToolHost` path. Standalone `Main`/`MainAsync` commands opt into the
same Ctrl+C / SIGTERM token only by declaring a `CancellationToken` parameter; without one
they skip System.CommandLine's invocation pipeline entirely (see
[Standalone commands](#standalone-commands-main--mainasync)).

## The Tool meta-package

```csharp
builder.UseDefaults(
    configOverridePath: null,          // optional per-user override file
    environmentVariablePrefix: null,   // optional env var prefix
    commandsAssembly: null);           // defaults to the entry assembly
```

is equivalent to:

```csharp
builder
    .UseSerilog()
    .UseVerbosityOptions()
    .UseObjectOutput()
    .AddCommandsFromAssembly(commandsAssembly ?? Assembly.GetCallingAssembly())
    .UseDefaultConfiguration(configOverridePath, environmentVariablePrefix);
// UseDefaultConfiguration adds appsettings.json, the override file, and env vars
```

Use it when you want the opinionated defaults; compose the individual `Use*` extensions when
you need finer control (for example when shipping a library of commands without Serilog, or
when you want `triaxis.CommandLine.ObjectOutput` to be trimmable).

The source-generated entry point does **not** call `UseDefaults`. Instead it chains the
individual helpers and omits `.UseObjectOutput()` when every `[Command]` class returns
`void`/`Task`/`int`/`Task<int>`, so the ObjectOutput + YamlDotNet graph becomes
unreachable and the trimmer can drop it. Keep that in mind if you add more work to
`UseDefaults`: it only runs for hand-written entry points that call it explicitly.

### Source-generated entry point

When the consuming project is an executable (`OutputType=Exe`) and has no user-written
`Main`, the source generator emits one for you. You can therefore delete `Program.cs` from
any tool that would otherwise contain nothing but the canonical one-liner — the generator
produces an equivalent `Main` that chains
`.UseSerilog().UseVerbosityOptions()[.UseObjectOutput()].UseDefaultConfiguration().AddCommandsFromAssembly(...).Run()`.
The `.UseObjectOutput()` call is emitted only when at least one `[Command]` class has a
return type other than `void`/`Task`/`int`/`Task<int>` — projects whose commands all
return one of those can trim `triaxis.CommandLine.ObjectOutput` (and `YamlDotNet`) out of
the published binary. The generator falls back to `AddCommandsFromAssembly().Run()` when
only the base `triaxis.CommandLine` package is referenced, without the `Tool`
meta-package.

Writing your own `Main` is always fine: if one already exists the generator skips
entry-point emission, so you never get a "multiple entry points" error.

The `UseDefaultConfiguration` parameters that the generator cannot infer on its own can
be supplied via MSBuild properties:

```xml
<PropertyGroup>
  <TriaxisCommandLineConfigOverridePath>MyTool/appsettings.json</TriaxisCommandLineConfigOverridePath>
  <TriaxisCommandLineEnvironmentVariablePrefix>MYTOOL_</TriaxisCommandLineEnvironmentVariablePrefix>
</PropertyGroup>
```

This composes naturally with .NET 10
[file-based apps](https://learn.microsoft.com/dotnet/core/tutorials/file-based-apps).
A complete tool in a single file, no project file, no `Main`:

```csharp
#!/usr/bin/env dotnet
#:package triaxis.CommandLine.Tool@*

[Command("greet", Description = "Say hello")]
public class GreetCommand : LoggingCommand
{
    [Option("--name", "-n")]
    private readonly string _name = "World";

    public void Execute() => Console.WriteLine($"Hello {_name}!");
}
```

See [`examples/hello.cs`](./examples/hello.cs) for a runnable version.

## Technical documentation

Deeper dives into how the library is put together live under [`docs/`](./docs):

- [Architecture overview](./docs/architecture.md)
- [Parameter binding](./docs/parameter-binding.md)
- [Command discovery and the source generator](./docs/source-generator.md)
- [Dependency injection and `[Inject]`](./docs/dependency-injection.md)
- [Hosting, `IHostBuilder`, and `ApplyTo`](./docs/hosting.md)
- [Middleware and the command executor](./docs/middleware.md)
- [Object output pipeline](./docs/object-output.md)

## Examples

Runnable examples live under [`examples/`](./examples):

- [`examples/Hello`](./examples/Hello) — single command, DI and verbosity flags. Has no
  `Program.cs` — the entry point is source-generated.
- [`examples/ObjectOutput`](./examples/ObjectOutput) — every supported return shape
  (`IEnumerable`, `IAsyncEnumerable`, `Task<IEnumerable>`, tuples, `DataTable`, manual
  `IObjectOutputHandler`) and the `--output` formatter matrix.
- [`examples/BindingShowcase`](./examples/BindingShowcase) — every parameter-binding
  variant (public/private, required, init-only, `[Options]` grouping, nested
  `[Options]`, collections, constructor injection, aliases, nested command paths) plus
  the `[ConfigureServices]` hook.
- [`examples/WebHost`](./examples/WebHost) — a standalone `MainAsync` subcommand that
  runs an ASP.NET Core server while sharing the CLI's configuration, Serilog wiring,
  and DI container via `IToolBuilder.ApplyTo(web.Host)`.
- [`examples/hello.cs`](./examples/hello.cs) — a single-file .NET 10 "dotnet run app.cs"
  tool (no `.csproj`, no `Main`, shebang-executable). MSBuild properties such as
  `TriaxisCommandLineEnvironmentVariablePrefix` can be supplied via `#:property` if
  the generated bootstrap needs them.

Build and run:

```shell
dotnet build examples/Examples.sln
dotnet run --project examples/Hello -- hello Alice
dotnet run --project examples/ObjectOutput -- enumerable -o Json
dotnet run --project examples/BindingShowcase -- ctor-inject --name Alice
dotnet run --project examples/WebHost -- serve --port 5000
dotnet run examples/hello.cs -- greet --name Alice
./examples/hello.cs greet --name Alice        # after chmod +x
```

## Building from source

```shell
dotnet build src/triaxis.CommandLine.sln
dotnet test  src/triaxis.CommandLine.sln
dotnet build examples/Examples.sln
```

## License

This package is licensed under the [MIT License](./LICENSE.txt).

Copyright &copy; 2023 triaxis s.r.o.
