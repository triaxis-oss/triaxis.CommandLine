# Logging and verbosity

`triaxis.CommandLine.Serilog` wires [Serilog](https://serilog.net/) into the tool's
`ILoggerFactory` and adds the `--verbosity` / `-v` / `-q` options. Both are part of
`UseDefaults()` and of the source-generated entry point; `UseDefaultLogging()` is the
one-liner that adds just the two (`UseSerilog()` + `UseVerbosityOptions()`).

```csharp
Tool.CreateBuilder(args)
    .UseSerilog()
    .UseVerbosityOptions()
    .AddCommandsFromAssembly()
    .Run();
```

## The logger is created lazily

`UseSerilog()` registers an `ILoggerProvider` **factory**, not a logger. The Serilog
pipeline is built the first time something resolves logging — by which time the command
line has been parsed and configuration assembled. That is what makes the following work
without a `LoggingLevelSwitch`:

- the `Serilog` section of `appsettings.json` is applied via `ReadFrom.Configuration`;
- the minimum level comes from the parsed `--verbosity` / `-v` / `-q`
  (`VerbosityOptions.GetEffectiveLevel(parseResult)`), applied *after* the configuration
  and after your own `configure` callback, so the command line always wins;
- `ParseResult` is resolved optionally, so the same provider works on an alternate host
  (e.g. a `WebApplication` reached through `ApplyTo`) where no `ParseResult` exists — there
  the level is whatever configuration produced.

See [Hosting integration](hosting.md#useserilog-and-hostbuildercontext) for the three
`UseSerilog` overloads and why the `HostBuilderContext` one is the primary shape.

## Verbosity options

`UseVerbosityOptions()` adds three recursive options to the root command, so they are
accepted at any level of the command tree:

| Flag | Effect |
| --- | --- |
| `--verbosity <Trace\|Debug\|Information\|Warning\|Error\|Critical>` | Set explicitly (default `Information`) |
| `-v`, `-vv` | Step up (`Debug`, then `Trace`) |
| `-q`, `-qq` | Step down (`Warning`, then `Error`) |

`-v` and `-q` are counted by scanning `ParseResult.Tokens`, so they stack (`-v -v` and
`-vv` are the same) and combine with an explicit `--verbosity` — each occurrence moves one
step from the level `--verbosity` set.

## The default console sink

A console sink is only added when configuration does **not** define `Serilog:WriteTo` — so
the moment `appsettings.json` declares sinks, they are the whole story and nothing is added
behind your back:

```json
{
  "Serilog": {
    "MinimumLevel": "Information",
    "WriteTo": [{ "Name": "Console" }]
  }
}
```

The built-in sink writes `[HH:mm:ss.fff LVL] Context: message`, sends every level to
stderr, and picks a theme from `FORCE_COLOR` (`AnsiConsoleTheme.Sixteen` when it asks for
16 colours, `Literate` otherwise), keeping colour on redirected output when `FORCE_COLOR`
is set. `UseSerilog(useShortContext: true)` enriches events with `ShortContext` — the
`SourceContext` after the last dot — and uses it in the template, which is usually what a
CLI wants to read.

To add sinks or enrichers without giving up the defaults, pass a `configure` callback:

```csharp
builder.UseSerilog((ctx, logger) => logger
    .Enrich.WithProperty("Tool", "mytool")
    .WriteTo.File(Path.Combine(ctx.Configuration["LogDir"]!, "mytool.log")));
```

## `LoggingCommand`

`LoggingCommand` (base package) is an optional base class that saves a command from
declaring its own logger:

```csharp
[Command("hello")]
public class HelloCommand : LoggingCommand
{
    public void Execute() => Logger.LogInformation("Hello!");
}
```

It takes an `[Inject]`ed `ILoggerFactory` and exposes `Logger` (cached, named after the
command type) plus `CreateLogger(name)` for a sub-logger named `{CommandType}.{name}`.
`LoggerScope(logger)` temporarily redirects `Logger` for the current async flow, which is
how a helper can log under the caller's name.

Commands that are not `LoggingCommand` just take `ILogger<T>` through `[Inject]` or the
constructor like any other service — see
[Dependency injection](dependency-injection.md).
