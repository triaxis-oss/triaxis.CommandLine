# Examples

Runnable tools, each one built around a different part of the library.

| Example | Shows |
| --- | --- |
| [`Hello`](Hello) | A single command, DI and the verbosity flags. Has no `Program.cs` — the entry point is source-generated. |
| [`ObjectOutput`](ObjectOutput) | Every supported return shape (`IEnumerable`, `IAsyncEnumerable`, `Task<IEnumerable>`, tuples, `DataTable`, manual `IObjectOutputHandler`) and the `--output` formatter matrix. |
| [`BindingShowcase`](BindingShowcase) | Every parameter-binding variant — public/private, required, init-only, `[Options]` grouping, nested `[Options]`, collections, constructor injection, aliases, nested command paths — plus the `[ConfigureServices]` hook. |
| [`WebHost`](WebHost) | A standalone `MainAsync` subcommand running an ASP.NET Core server while sharing the CLI's configuration, Serilog wiring and DI container via `IToolBuilder.ApplyTo(web.Host)`. See its [README](WebHost/README.md). |
| [`hello.cs`](hello.cs) | A single-file .NET 10 tool — no `.csproj`, no `Main`, shebang-executable. MSBuild properties such as `TriaxisCommandLineEnvironmentVariablePrefix` are supplied via `#:property`. |

```shell
dotnet build examples/Examples.sln
dotnet run --project examples/Hello -- hello Alice
dotnet run --project examples/ObjectOutput -- enumerable -o Json
dotnet run --project examples/BindingShowcase -- ctor-inject --name Alice
dotnet run --project examples/WebHost -- serve --port 5000
dotnet run examples/hello.cs -- greet --name Alice
./examples/hello.cs greet --name Alice        # after chmod +x
```

The examples target `net8.0` (`WebHost` and `hello.cs` need `net10.0`); the libraries
themselves target `netstandard2.0` and `netstandard2.1`.
