# triaxis.CommandLine

Write a class, get a command. An opinionated layer over
[System.CommandLine](https://learn.microsoft.com/dotnet/standard/commandline/) that
discovers commands with a source generator, binds their arguments and options, resolves
their dependencies, and formats whatever they return — with no `Program.cs` of your own and
no reflection at runtime.

```csharp
#!/usr/bin/env -S dotnet --
#:package triaxis.CommandLine.Tool@*

[Command("greet", Description = "Say hello")]
public class GreetCommand : LoggingCommand
{
    [Option("--name", "-n")]
    private readonly string _name = "World";

    public void Execute() => Console.WriteLine($"Hello {_name}!");
}
```

```shell
./hello.cs greet --name Alice    # a whole tool in one file, no project, no Main
```

That is the whole tool: `--help`, `--verbosity`, `--output`, Ctrl+C handling, DI and
configuration come with it.

## Install

```shell
dotnet new console -n MyTool && cd MyTool
dotnet add package triaxis.CommandLine.Tool
```

**Delete `Program.cs`.** With no entry point of its own, the project gets a generated one
that wires up logging, configuration, object output and command discovery — and omits what
your commands never use. Add a `[Command]` class anywhere in the assembly and run it.

[Getting started](docs/getting-started.md) walks through the rest: commands and
subcommands, argument and option binding, `[Inject]` and constructor DI, configuration
files and scopes, and the `UseDefaults()` one-liner for a hand-written `Main`.

| Package | Purpose |
| --- | --- |
| `triaxis.CommandLine` | Core `ToolBuilder`, attributes, command discovery, DI |
| `triaxis.CommandLine.ObjectOutput` | `--output` formatters (Table/Wide/Json/Yaml/Raw/None) |
| `triaxis.CommandLine.Serilog` | Serilog integration and `--verbosity` / `-v` / `-q` options |
| `triaxis.CommandLine.Tool` | Opinionated all-in-one meta-package (`UseDefaults()`) |

The libraries target `netstandard2.0` and `netstandard2.1`, so they run on any modern .NET
or .NET Framework host. Tools built on top typically target `net8.0` or newer.

## What you get

| | |
| --- | --- |
| Commands | `[Command("db", "migrate")]` on a class with `Execute`/`ExecuteAsync`. Nested paths become subcommands, and a class can carry several. Discovery is source-generated. |
| Binding | `[Argument]` and `[Option]` on fields or properties, public or private; `[Options]` flattens a nested object; `[ActionOption]` adds an alternate entry point behind its own flag. |
| Dependency injection | Constructor parameters resolve from the container without registering the command, or use `[Inject]` on any member. `[ConfigureServices]` / `[Configure]` hooks register services without a hand-written `Main`. |
| Configuration | `appsettings.json`, machine and per-user overrides, environment variables — layered into precedence scopes, with `Update(scope, …)` writing one layer back as a minimal edit. |
| Logging | Serilog, created lazily *after* parsing, so `-v` / `-q` / `--verbosity` and the `Serilog` config section both apply with no level switch. |
| Output | Return a record, a list, an `IAsyncEnumerable` or a tuple; `--output Table\|Wide\|Json\|Yaml\|Raw\|None` formats it. JSON and YAML are emitted in-house, so there is no serializer dependency. |
| Middleware | `AddMiddleware(async (context, next) => …)` around every invocation, first registered outermost. |
| Errors and Ctrl+C | `CommandErrorException` (and any type you map to it) exits cleanly with a logged message and your exit code; `ExecuteAsync(CancellationToken)` gets cooperative shutdown. |
| Own host | A `Main`/`MainAsync` command skips the CLI's container and runs its own — ASP.NET Core inside a subcommand, sharing the tool's config and Serilog via `ApplyTo`. |
| NativeAOT | `PublishAot` works: 3.38 MiB for a command-only tool, no trim warnings. |

## Documentation

| | |
| --- | --- |
| [Getting started](docs/getting-started.md) | packages, the first tool, commands, binding, DI and configuration in one pass |
| [Architecture](docs/architecture.md) | the whole pipeline, from `Tool.CreateBuilder` to result finalization |
| [Parameter binding](docs/parameter-binding.md) | attributes, naming, ordering, nested option groups, alternate entry points |
| [Source generator](docs/source-generator.md) | what is emitted per command, the command tree, and the generated entry point |
| [Dependency injection](docs/dependency-injection.md) | how the provider is assembled and how `[Inject]` is resolved |
| [Hosting](docs/hosting.md) | `IHostBuilder` conformance, configuration scopes, `ToolHost`, standalone commands, `ApplyTo` |
| [Middleware](docs/middleware.md) | the chain, `ICommandExecutor`, error mapping, cancellation |
| [Logging](docs/logging.md) | Serilog wiring, verbosity flags, the default console sink, `LoggingCommand` |
| [Object output](docs/object-output.md) | descriptors, formatters, the JSON and YAML emitters, trimming |
| [NativeAOT](docs/nativeaot.md) | what it costs, what to switch off, and what does not work |

Runnable projects for all of it live under [`examples/`](examples) — a hello-world tool,
the binding showcase, the formatter matrix, an ASP.NET Core subcommand, and a single-file
tool with no project at all.

## Building from source

```shell
dotnet build src/triaxis.CommandLine.sln
dotnet test  src/triaxis.CommandLine.sln
dotnet test  src/triaxis.CommandLine.sln -f net48   # needs mono on Linux
dotnet build examples/Examples.sln
```

## License

This package is licensed under the [MIT License](LICENSE.txt).

Copyright &copy; 2023 triaxis s.r.o.
