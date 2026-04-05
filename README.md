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

`UseDefaults()` composes `UseSerilog()`, `UseVerbosityOptions()`, `UseObjectOutput()` and
`AddCommandsFromAssembly()` — see [The `Tool` meta-package](#the-tool-meta-package) below.

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

Commands are discovered via `AddCommandsFromAssembly()`. Discovery is entirely
source-generated — the `triaxis.CommandLine` package ships a Roslyn source generator under
`analyzers/` that emits one `AsynchronousCommandLineAction` subclass per command plus a
`[ModuleInitializer]` that registers them all. `AddCommandsFromAssembly` throws if no
generated registration is present, which in practice only happens if the assembly was
compiled without a reference to the package.

### Arguments and options

Bind parsed values to **fields or properties** using `[Argument]` (positional) or `[Option]`
(named). Both derive from `CommandlineAttribute`, which exposes common metadata:

| Property | Meaning |
| --- | --- |
| `Name` | Explicit name. Defaults to the member name. |
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
as `IConfiguration`. `UseDefaults()` wires it up with:

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
executor logs it and exits with code `-1` without a stack trace:

```csharp
throw new CommandErrorException("File {Path} was not found", path);
```

Any other exception bubbles up to System.CommandLine's default handler, which prints the
exception and returns a non-zero exit code. You can replace the executor entirely by
registering your own `ICommandExecutor` in `ConfigureServices`.

### Cancellation

Commands declared as `ExecuteAsync(CancellationToken)` receive the token that
System.CommandLine raises on Ctrl+C / SIGTERM and get cooperative shutdown — including a
configurable termination timeout. Commands that do not accept a token get a
`Environment.FailFast(null)` callback registered on the token instead, so pressing Ctrl+C
during a non-cancellable command terminates the process immediately. The registration is
disposed as soon as the command body returns, so it never fires during middleware or
result finalization.

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
    .AddCommandsFromAssembly(commandsAssembly ?? Assembly.GetCallingAssembly());
// + appsettings.json, override file, and env vars wired into Configuration
```

Use it when you want the opinionated defaults; compose the individual `Use*` extensions when
you need finer control (for example when shipping a library of commands without Serilog).

### Source-generated entry point

When the consuming project is an executable (`OutputType=Exe`) and has no user-written
`Main`, the source generator emits one for you. You can therefore delete `Program.cs` from
any tool that would otherwise contain nothing but the canonical one-liner — the generator
produces an equivalent `Main` that calls
`Tool.CreateBuilder(args).UseDefaults(…).Run()` (or falls back to
`AddCommandsFromAssembly().Run()` when only the base `triaxis.CommandLine` package is
referenced, without the `Tool` meta-package).

Writing your own `Main` is always fine: if one already exists the generator skips
entry-point emission, so you never get a "multiple entry points" error.

The remaining `UseDefaults` parameters that the generator cannot infer on its own can be
supplied via MSBuild properties:

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
- [Middleware and the command executor](./docs/middleware.md)
- [Object output pipeline](./docs/object-output.md)

## Examples

Runnable examples live under [`examples/`](./examples):

- [`examples/Hello`](./examples/Hello) — single command, DI and verbosity flags. Has no
  `Program.cs` — the entry point is source-generated.
- [`examples/ObjectOutput`](./examples/ObjectOutput) — every supported return shape
  (`IEnumerable`, `IAsyncEnumerable`, `Task<IEnumerable>`, tuples, `DataTable`, manual
  `IObjectOutputHandler`) and the `--output` formatter matrix.
- [`examples/hello.cs`](./examples/hello.cs) — a single-file .NET 10 "dotnet run app.cs"
  tool (no `.csproj`, no `Main`, shebang-executable). Uses `[assembly: ToolDefaults(...)]`
  to configure the generated bootstrap.

Build and run:

```shell
dotnet build examples/Examples.sln
dotnet run --project examples/Hello -- hello Alice
dotnet run --project examples/ObjectOutput -- enumerable -o Json
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
