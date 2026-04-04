# CLAUDE.md

## Project

Opinionated extensions on top of System.CommandLine for building CLI tools with
attribute-based command discovery and dependency injection.

## Structure

```
src/triaxis.CommandLine/               Base package (System.CommandLine + M.E.DI + M.E.Configuration)
src/triaxis.CommandLine.ObjectOutput/  Output formatters (Table, JSON, YAML, Raw)
src/triaxis.CommandLine.Serilog/       Serilog logging + verbosity options
src/triaxis.CommandLine.Tool/          Opinionated all-in-one (UseDefaults)
examples/                              Hello and ObjectOutput examples
```

## Build & Test

```
dotnet build src/triaxis.CommandLine.sln
dotnet test src/triaxis.CommandLine.sln
dotnet build examples/Examples.sln
```

Examples target net8.0; the libraries target netstandard2.0 and netstandard2.1.

## Code Style

- **Braces**: Always use braces for `if`, `foreach`, `try`, etc. — even single-line bodies.
- **LangVersion**: 14. Use collection expressions (`[]`), file-scoped namespaces, primary
  constructors, `is not null` over `!= null`, etc.
- **Nullable**: Enabled everywhere. No `#nullable disable`.
- **Extension methods**: Take the concrete `ToolBuilder` to enable fluent chaining.
  Return `ToolBuilder` for the same reason.
- **Naming**: Follow .NET Framework Design Guidelines. No `Is` prefix on boolean
  properties (use `Required`, `Implicit`, `Hidden` — not `IsRequired`, etc.).

## Architecture

### ToolBuilder

`ToolBuilder` owns builder state: `IServiceCollection`, middleware list,
`IConfigurationManager`, and the `RootCommand` tree. `Run`/`RunAsync` builds the
`ServiceProvider`, then delegates to `ParseResult.Invoke`/`InvokeAsync` — System.CommandLine
handles dispatch, Ctrl+C, and default exception handling.

### DependencyCommandAction

Set as `Command.Action` during command discovery. On invocation, resolves the command
type from DI, injects `[Inject]` properties, binds `[Argument]`/`[Option]` members,
invokes `Execute`/`ExecuteAsync`, and delegates to `ICommandExecutor` for middleware
and result finalization.

### ICommandExecutor / DefaultCommandExecutor

Registered in DI. Runs the middleware chain around command execution, then calls
`EnsureCompleteAsync` on the result. Catches `CommandErrorException` and logs it.
Replaceable via `ConfigureServices`.

### Middleware

`AddMiddleware(async (context, next) => { ... })` wraps command execution.
First registered = outermost. ObjectOutput adds a middleware that formats the result
after `next()`.

### Cancellation

System.CommandLine's `ProcessTerminationTimeout` handles Ctrl+C/SIGTERM and passes
a `CancellationToken` to the action. Commands accepting `CancellationToken` get
cooperative cancellation. Commands that don't trigger `Environment.FailFast`
(the registration is disposed after the command returns so it doesn't affect middleware).

### Serilog Integration

The Serilog logger is created lazily inside a DI factory (`ILoggerProvider` singleton).
At resolution time, `ParseResult` and `IConfiguration` are available, so verbosity
and `ReadFrom.Configuration` work without a `LoggingLevelSwitch`. No middleware needed
for logging — the level is baked into the logger at creation time.

## Key Types

- `IToolBuilder` — public interface for the builder (Run, RunAsync, AddMiddleware, ConfigureServices)
- `ToolBuilder` — concrete builder, owns `IServiceCollection`, `IConfigurationManager`
- `DependencyCommandAction` — `AsynchronousCommandLineAction` that resolves from DI
- `InvocationContext` — passed through middleware: Services, ParseResult, CommandType,
  InvocationResult, ExitCode, CancellationToken
- `ICommandExecutor` / `DefaultCommandExecutor` — runs middleware chain + finalization
- `ICommandInvocationResult` / `ICommandInvocationResult<T>` — wraps command return
  values for streaming enumeration (used by ObjectOutput)
- `VerbosityOptions` — public static option definitions for `--verbosity`/`-v`/`-q`

## Dependencies

- **Base**: System.CommandLine, M.E.DependencyInjection, M.E.Configuration, M.E.Logging
- **ObjectOutput**: + YamlDotNet, M.E.Options, M.E.DI.Abstractions
- **Serilog**: + Serilog, Serilog.Extensions.Logging, Serilog.Sinks.Console,
  Serilog.Settings.Configuration
- **Tool**: + M.E.Configuration.Json, M.E.Configuration.EnvironmentVariables,
  M.E.FileProviders.Physical; references Serilog + ObjectOutput
