# Technical documentation

Deep-dive notes on how `triaxis.CommandLine` is put together. These are intended for
contributors, advanced users, and anyone wanting to extend the library.

The top-level [`README.md`](../README.md) covers installation and day-to-day usage. Start
there if you haven't yet.

- [Architecture overview](architecture.md) — the full pipeline from `Tool.CreateBuilder`
  through `IHostBuilder.Build` and `ParseResult.Invoke` to `EnsureCompleteAsync`.
- [Parameter binding](parameter-binding.md) — how `[Argument]` / `[Option]` / `[Options]`
  are turned into System.CommandLine symbols and bound back onto instances, including
  nested option objects and `required` members.
- [Command discovery and the source generator](source-generator.md) — how the generator
  emits per-command action classes, `UnsafeAccessor` vs reflection fallbacks, and the
  module initializer that wires everything up.
- [Dependency injection and `[Inject]`](dependency-injection.md) — how the service
  provider is built, how commands are resolved via `ActivatorUtilities`, and how
  `[Inject]` members, `ILogger`, `CancellationToken`, and hosted services flow through.
- [Middleware and the command executor](middleware.md) — the middleware chain,
  `ICommandExecutor`, `InvocationContext`, error handling, and Ctrl+C semantics.
- [Object output pipeline](object-output.md) — `ICommandInvocationResult<T>`, streaming,
  descriptors, formatters, and the `--output` flag.
- [Hosting integration](hosting.md) — `IHostBuilder` conformance, `ToolHost`,
  `HostBuilderContext.GetInvocationContext()`, and `IHostedService` lifecycle.
