# Documentation

[Getting started](getting-started.md) is the guided tour — install, first command,
binding, DI, configuration, and a pointer into each of the pages below. The top-level
[`README.md`](../README.md) is the short version of that.

The rest are deep dives into how the library is put together, for contributors, advanced
users, and anyone extending it.

| | |
| --- | --- |
| [Getting started](getting-started.md) | packages, the first tool, commands, binding, DI and configuration in one pass |
| [Architecture overview](architecture.md) | the full pipeline from `Tool.CreateBuilder` through `IHostBuilder.Build` and `ParseResult.Invoke` to `EnsureCompleteAsync` |
| [Parameter binding](parameter-binding.md) | how `[Argument]` / `[Option]` / `[Options]` become System.CommandLine symbols and are bound back, including nested option objects, `required` members and `[ActionOption]` |
| [Command discovery and the source generator](source-generator.md) | what the generator emits per command, `UnsafeAccessor` vs the reflection fallback, the command tree, and the generated entry point |
| [Dependency injection and `[Inject]`](dependency-injection.md) | how the provider is assembled, how commands are constructed, and how `[Inject]`, `ILogger`, `CancellationToken` and hosted services flow through |
| [Hosting integration](hosting.md) | `IHostBuilder` conformance, configuration sources and scopes, `ToolHost`, standalone commands and `ApplyTo` |
| [Middleware and the command executor](middleware.md) | the middleware chain, `ICommandExecutor`, `InvocationContext`, error mapping and Ctrl+C semantics |
| [Logging and verbosity](logging.md) | Serilog wiring, `--verbosity` / `-v` / `-q`, the default console sink, `LoggingCommand` |
| [Object output pipeline](object-output.md) | `ICommandInvocationResult<T>`, streaming, descriptors, formatters, the JSON and YAML emitters, and the `--output` flag |
| [NativeAOT](nativeaot.md) | publishing trimmed and ahead-of-time, what it costs and what it cannot do |
