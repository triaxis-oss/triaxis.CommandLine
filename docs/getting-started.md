# Getting started

A tour of everything a tool normally needs, with a pointer to the deep dive for each
topic. The [top-level `README.md`](../README.md) is the short version of this page.

## Packages

| Package | Purpose |
| --- | --- |
| `triaxis.CommandLine` | Core `ToolBuilder`, attributes, command discovery, DI |
| `triaxis.CommandLine.ObjectOutput` | `--output` formatters (Table/Wide/Json/Yaml/Raw/None) |
| `triaxis.CommandLine.Serilog` | Serilog integration and `--verbosity` / `-v` / `-q` options |
| `triaxis.CommandLine.Tool` | Opinionated all-in-one meta-package (`UseDefaults()`) |

The libraries target `netstandard2.0` and `netstandard2.1`, so they can be consumed from
any modern .NET or .NET Framework host. Tools built on top typically target `net8.0` or
newer.

## First tool

```shell
dotnet new console -n MyTool
cd MyTool
dotnet add package triaxis.CommandLine.Tool
```

**Delete `Program.cs`.** When the project is an executable with no user-written entry
point, the source generator synthesizes one — that is the canonical setup, and the one to
prefer. It chains the individual helpers rather than calling `UseDefaults()`, which lets it
omit the pieces your tool doesn't use, so the formatter stack can be trimmed out of a tool
whose commands all return `void`/`int`. See [Generated entry
point](source-generator.md#generated-entry-point).

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

```shell
dotnet run -- hello
dotnet run -- hello --name Alice
dotnet run -- hello -n Alice -v           # -v raises log level to Debug
dotnet run -- hello --help                # System.CommandLine generated help
```

A hand-written entry point is still fine when you need to do something before the builder
runs — if a `Main` already exists the generator skips entry-point emission, so there is
never a "multiple entry points" error:

```csharp
return Tool.CreateBuilder(args).UseDefaults().Run();
```

but it opts out of that tailoring: `UseDefaults()` pulls in the whole stack
unconditionally. Chain the helpers yourself if you care.

### The `Tool` meta-package

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

Use it when you want the opinionated defaults; compose the individual `Use*` extensions
when you need finer control — for example when shipping a library of commands without
Serilog, or when you want `triaxis.CommandLine.ObjectOutput` to be trimmable. Note that the
generated entry point does **not** call `UseDefaults`, so work added there only runs for
hand-written entry points that call it explicitly.

The parameters the generator cannot infer are supplied via MSBuild properties, which also
works from a `#:property` line in a [file-based
app](https://learn.microsoft.com/dotnet/core/tutorials/file-based-apps):

```xml
<PropertyGroup>
  <TriaxisCommandLineConfigOverridePath>MyTool/appsettings.json</TriaxisCommandLineConfigOverridePath>
  <TriaxisCommandLineEnvironmentVariablePrefix>MYTOOL_</TriaxisCommandLineEnvironmentVariablePrefix>
</PropertyGroup>
```

## Commands

A command is any class annotated with `[Command]` exposing a public `Execute` or
`ExecuteAsync` method. The generator emits a direct `new MyCommand(...)` per command,
resolving each constructor parameter from the container, so constructor injection works
without the class being registered — and without reflective activation. Annotate a
constructor with `[ActivatorUtilitiesConstructor]` to pick it when there is more than one.

```csharp
[Command("db", "migrate", Description = "Apply pending migrations")]
public class MigrateCommand
{
    public Task<int> ExecuteAsync(CancellationToken cancellationToken) { /* ... */ }
}
```

- The path can have one or more segments — nested segments become subcommands
  (`mytool db migrate`). With **no** segments at all (`[Command]`) the class becomes the
  root command, what runs when the tool is invoked with no verb.
- A top-level command (or one of its aliases) must not reuse the executable's own name;
  the generator reports that as `TXCL007`. A name that can never be typed — empty, or
  containing whitespace — is `TXCL008`.
- `[Command]` is `AllowMultiple = true`, so one class can be exposed under several paths.
  It can **also** be applied at the **assembly** level to attach a description or aliases
  to an intermediate tree node with no dedicated class
  (`[assembly: Command("db", Description = "Database operations")]`).
- Supported return types: `void`, `int`, `Task`, `Task<int>`, `ICommandInvocationResult`,
  `Task<ICommandInvocationResult>`, and — when `UseObjectOutput` is enabled — any `T`,
  `IEnumerable<T>`, `IAsyncEnumerable<T>`, `Task<T>`, `Task<IEnumerable<T>>`, and
  `System.Data.DataTable` (the last is unavailable under [NativeAOT](nativeaot.md)).
- `[SupportedOSPlatform("windows"|"linux"|"macos"|…)]` on a command class (or a base class)
  gates its registration to those platforms; multiple attributes combine with a logical OR.

Commands are discovered via `AddCommandsFromAssembly()`, which throws if the assembly
carries no generated registration — in practice only when it was compiled without a
reference to the package. Details in [Command discovery and the source
generator](source-generator.md).

### Standalone commands (`Main` / `MainAsync`)

A `[Command]` class can declare `Main` / `MainAsync` instead of `Execute`/`ExecuteAsync`.
The generator then skips the DI container and middleware pipeline entirely and hands the
`IToolBuilder` through, so the command can stand up its own host —
`IToolBuilder.ApplyTo(IHostBuilder)` replays the tool's configuration sources and service
registrations onto it:

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

Declaring a `CancellationToken` opts the command into System.CommandLine's
process-termination handling; omit it and the command owns its lifecycle outright.
Standalone commands still bind `[Argument]`/`[Option]`/`[Options]`, but cannot use
`[Inject]` or constructor DI — their whole point is that no service provider is built on
the CLI side. Full walkthrough in [Hosting
integration](hosting.md#standalone-commands-main--mainasync) and
[`examples/WebHost`](../examples/WebHost).

## Arguments and options

Bind parsed values to **fields or properties** with `[Argument]` (positional) or
`[Option]` (named). Both derive from `CommandlineAttribute`, which carries `Name`
(defaulting to the member name in kebab-case), `Description` and `Order`.
`ArgumentAttribute` adds `Required`, `OptionAttribute` adds `Aliases` and `Required`, and
the `required` C# keyword is honoured automatically.

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

```shell
mytool copy ./a.txt ./b.txt --force --retries 5
```

`[Options]` on a property flattens a nested object's `[Option]`/`[Argument]` members into
the command, and `[ActionOption]` on a method exposes an alternate entry point behind its
own flag (`backup --list` runs `ListAsync` with everything bound as the primary would have
seen it). Both, plus type support and ordering rules, are in [Parameter
binding](parameter-binding.md).

## Dependency injection

Register services with `ConfigureServices`, or — when relying on the generated entry point
— with a static `[ConfigureServices]` hook anywhere in the assembly. `[Configure]` is its
bigger sibling for customizing the builder or host itself, and a static `Configure` method
on a command type runs only when that command is invoked:

```csharp
public static class Startup
{
    [ConfigureServices]
    public static void Register(IServiceCollection services)
        => services.AddSingleton<IMyService, MyService>();
}
```

Inside a command, take services through the constructor as usual, or use `[Inject]` on any
field or property — handy on reusable base classes (see `LoggingCommand`) so derived
commands don't forward dependencies through their own constructors:

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

See [Dependency injection and `[Inject]`](dependency-injection.md), which also covers
what a `[Configure]` hook turns off in the generated entry point.

## Configuration

`IToolBuilder.Configuration` is an `IConfigurationManager`, also registered into DI as
`IConfiguration`. `UseDefaults()` — and the `UseDefaultConfiguration()` helper it is made
of — wires up `appsettings.json` next to the executable, optional machine and per-user
override files, and an optional environment-variable prefix:

```csharp
Tool.CreateBuilder(args)
    .UseDefaults(
        configOverridePath: "MyTool/appsettings.json",
        environmentVariablePrefix: "MYTOOL_")
    .Run();
```

`ConfigureConfiguration` adds your own sources fluently (the two-argument overload is
deferred to `Build()` and can branch on the parsed command line), `UseScopedConfiguration`
groups sources into precedence scopes and can remap a subtree onto the root, and
`IConfiguration.Update(scope, …)` persists a change back to one scope's file as a minimal
edit that keeps comments and key order. All of it is in [Hosting
integration](hosting.md#configureconfiguration).

## Logging, output, middleware, errors

Each of these has its own page — the one-paragraph version:

- **Logging** — `UseSerilog()` creates the logger lazily, after parsing, so
  `--verbosity` / `-v` / `-q` and the `Serilog` configuration section both apply with no
  `LoggingLevelSwitch`. [logging.md](logging.md)
- **Object output** — `UseObjectOutput()` adds a recursive `--output` / `-o` option and a
  middleware that formats whatever the command returns as `Table`, `Wide`, `Json`, `Yaml`,
  `Raw` or `None`. `[ObjectOutput]` controls field visibility, ordering and formatting.
  [object-output.md](object-output.md)
- **Middleware** — `AddMiddleware(async (context, next) => …)` wraps every invocation,
  first registered outermost. [middleware.md](middleware.md)
- **Errors** — throw `CommandErrorException` for a user-facing failure (logged, no stack
  trace, `ExitCode` per throw), or map other exception types to the same treatment with
  `builder.MapException<T>(…)`.
  [middleware.md](middleware.md#error-handling)
- **Cancellation** — a command declaring `ExecuteAsync(CancellationToken)` gets the Ctrl+C
  / SIGTERM token and cooperative shutdown; one that doesn't gets a `FailFast` registration
  for the duration of its body. [middleware.md](middleware.md#cancellation)

## Where to go next

- [Architecture overview](architecture.md) — the whole pipeline on one page.
- [NativeAOT](nativeaot.md) — publishing trimmed and ahead-of-time.
- [`examples/`](../examples) — runnable projects for each of the above.
