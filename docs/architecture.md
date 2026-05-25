# Architecture overview

`triaxis.CommandLine` is a thin, opinionated layer over
[System.CommandLine](https://learn.microsoft.com/dotnet/standard/commandline/) and
`Microsoft.Extensions.Hosting`. It does not replace the parser or the invocation pipeline —
it plugs into them through the same extension points that any System.CommandLine consumer
would use. The value add is in **how commands are discovered, how parameters are bound, how
services are resolved, and how results are turned into output**.

## High-level pipeline

```
Tool.CreateBuilder(args)
      │
      ▼
IToolBuilder  ─ (IHostBuilder)
      │  .UseSerilog() / .UseVerbosityOptions() / .UseObjectOutput() / .UseDefaultConfiguration() / .AddCommandsFromAssembly()
      │  .ConfigureServices(...) / .ConfigureAppConfiguration(...) / .AddMiddleware(...)
      ▼
Run() / RunAsync()     ← extension methods on IToolBuilder
      │
      ├─ IHostBuilder.Build()          → ToolHost
      │       ├─ RootCommand.Parse(args)      (System.CommandLine parser → ParseResult)
      │       ├─ HostBuilderContext created, build-time InvocationContext (ParseResult only)
      │       ├─ ConfigureAppConfiguration callbacks run
      │       ├─ ConfigureServices(HostBuilderContext) callbacks run
      │       ├─ ParseResult, IConfiguration, IHostApplicationLifetime, ILoggerFactory, ICommandExecutor registered
      │       └─ ServiceProvider built
      │
      ├─ ToolHost.StartAsync()        → starts IHostedService instances in order
      ├─ ToolHost.Invoke() / InvokeAsync()
      │       └─ ParseResult.Invoke() / InvokeAsync()
      │               └─ {Command}.Action.InvokeAsync()   (source-generated)
      │                       ├─ new InvocationContext(services, parseResult, ct, commandType)
      │                       ├─ ICommandExecutor.ExecuteAsync(context, body)
      │                       │       ├─ wraps body in the middleware chain
      │                       │       └─ body:
      │                       │               ├─ new T(...) with DI-resolved ctor args
      │                       │               ├─ inlined [Inject] assignments
      │                       │               ├─ foreach/switch over CommandResult.Children
      │                       │               ├─ Execute / ExecuteAsync (with optional FailFast)
      │                       │               └─ wrap result into context.InvocationResult
      │                       └─ return context.ExitCode
      │
      ├─ await context.InvocationResult.EnsureCompleteAsync(ct)   (streaming / finalization)
      └─ ToolHost.StopAsync()        → stops IHostedService instances in reverse order
                                       and disposes the ServiceProvider
```

## Key types

| Type | Responsibility |
| --- | --- |
| `Tool` | Static entry point — `CreateBuilder(args)`. |
| `IToolBuilder` / `ToolBuilder` | Owns `IServiceCollection`, middleware list, `IConfigurationManager`, the `RootCommand` tree, and `IHostBuilder` state (properties, configuration callbacks). |
| `ToolBuilderRunExtensions` | Extension methods (`Run`, `RunAsync`) that drive `Build → Start → Invoke → Stop → Dispose`. |
| `ToolHost` | `IHost` + `IHostApplicationLifetime` implementation — starts/stops `IHostedService`s, owns the `ServiceProvider`, fires lifetime tokens, exposes `Invoke`/`InvokeAsync`. |
| `CommandTreeNode` | Lightweight model describing the command tree. Returned by the source-generated factory, merged into `RootCommand` via `ApplyTo()`. |
| `ArgumentDefinition<T>` / `OptionDefinition<T>` | Type-safe descriptors in the tree model. `Create()` produces fresh System.CommandLine `Argument<T>`/`Option<T>` instances. |
| Generated `{Command}.Action` classes | One `AsynchronousCommandLineAction` nested in a per-command `internal static class {Command}` umbrella. Emitted by `triaxis.CommandLine.SourceGenerator`. Constructs the command via the umbrella's `CreateInstance` / `BindOptions` / `InjectServices` lifecycle, then invokes `Execute`/`ExecuteAsync`. |
| `ICommandExecutor` / `DefaultCommandExecutor` | Runs the middleware chain around command execution, finalizes the result, and turns mapped exceptions into a clean exit. |
| `ExceptionMapper` / `CommandError` | A registered `ExceptionMapper` maps an exception to a `CommandError` (exit code + structured-logging message) for a clean exit instead of an unhandled crash. |
| `InvocationContext` | Passed through middleware: `Services`, `ParseResult`, `CommandType`, `InvocationResult`, `ExitCode`, `CancellationToken`. |
| `ICommandInvocationResult[<T>]` | Uniform wrapper around anything a command can return. |

## Separation of concerns

**System.CommandLine owns:**

- Argument/option parsing (tokenization, type conversion, help, suggestions)
- The subcommand tree and help rendering
- Ctrl+C / `ProcessTerminationTimeout` handling and `CancellationToken` flow
- Default error handling if nothing else catches an exception

**`Microsoft.Extensions.Hosting` owns:**

- Configuration composition (`HostBuilderContext`, `ConfigureAppConfiguration`)
- `IHostedService` startup/shutdown lifecycle
- The `HostBuilderContext.Properties` bag

**triaxis.CommandLine owns:**

- Command discovery from `[Command]` attributes via the source generator
- Constructing command instances via `new T(...)` with DI-resolved constructor args
- Transferring parsed values from `ParseResult` onto command instances (via a
  `foreach`/`switch` over `CommandResult.Children`, with `UnsafeAccessor` or
  `FieldInfo`/`PropertyInfo` fallbacks for non-public members)
- Resolving `[Inject]` members and calling the right `Execute` overload
- Wrapping return values in `ICommandInvocationResult` and running the middleware chain
- Optional layers: Serilog wiring, verbosity flags, object output formatters

Because the library only plugs into standard extension points, you can mix-and-match: add
raw `Command` objects manually, use your own `Option<T>`/`Argument<T>` instances, register
`IHostedService` instances through the usual `ConfigureServices` calls, replace
`ICommandExecutor`, swap Serilog for another `ILoggerFactory`, etc.

## Service provider lifecycle

A single `IServiceProvider` is built inside `IHostBuilder.Build()` and wrapped in a
`ToolHost`. `ToolHost` implements both `IDisposable` and `IAsyncDisposable`; `RunAsync`
goes through `DisposeAsync` so containers holding `IAsyncDisposable`-only services shut
down cleanly. Sync `Run` keeps sync semantics — register async-only disposables only
when using `RunAsync`.

`ParseResult` is registered as a **singleton** during build so that anything resolved later
(loggers, option binders, formatter providers) can read parsed values. This is how
`UseSerilog` reads `--verbosity`, how `UseObjectOutput` picks a formatter from `--output`,
and how you can read parsed global options from any service.

A **build-time `InvocationContext`** (containing only `ParseResult`, with `Services` and
`CommandType` left null) is also stashed in `HostBuilderContext.Properties` under a known
key so `ConfigureAppConfiguration` / `ConfigureServices(HostBuilderContext, ...)` callbacks
can read parsed arguments via `HostBuilderContext.GetInvocationContext()`. The "real"
`InvocationContext` with a populated `Services` and `CommandType` is created later by the
generated command action when the command actually runs. See [hosting.md](hosting.md).

## Cancellation flow

1. System.CommandLine creates a `CancellationTokenSource` for the invocation and subscribes
   to `Console.CancelKeyPress` / `AppDomain.ProcessExit`. This is entirely stock behaviour.
2. The token is passed to the generated action's
   `InvokeAsync(parseResult, cancellationToken)`.
3. If the command has an `ExecuteAsync(CancellationToken)` overload, that token is forwarded
   and cancellation is cooperative.
4. Otherwise the generated action registers `Environment.FailFast(null)` on the token so a
   Ctrl+C during a non-cancellable command terminates the process. The registration is
   disposed immediately after the command body returns, so it never interferes with
   middleware or result finalization.
5. `InvocationResult.EnsureCompleteAsync(ct)` also receives the token — streaming object
   output can therefore honour Ctrl+C mid-enumeration.

## Where to go next

- [Parameter binding](parameter-binding.md) — how members become `Option<T>`/`Argument<T>`
  and how values flow back.
- [Command discovery and the source generator](source-generator.md) — what the generator
  emits and how it reaches non-public members.
- [Middleware and the command executor](middleware.md) — chain construction and error
  handling.
- [Hosting integration](hosting.md) — `IHostBuilder` conformance and `IHostedService`
  lifecycle.
