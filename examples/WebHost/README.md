# WebHost example

Demonstrates running a web server as a subcommand alongside regular CLI commands,
sharing the same configuration and logging wiring.

```text
status  →  ExecuteAsync(), runs through the default ToolHost (DI, middleware).
serve   →  MainAsync(IToolBuilder, CancellationToken), runs its own WebApplication
           with builder.ApplyTo(web.Host) replaying every CLI-side registration.
```

Key points:

- **Services registered once, at the builder level** (`Program.cs`): `IGreeter` is
  declared with `.ConfigureServices(s => s.AddSingleton<IGreeter, ConfigurableGreeter>())`
  and is available to both the CLI command and the ASP.NET Core endpoints.
- **Identical Serilog formatting** across both commands. The standalone `serve`
  command calls `web.Logging.ClearProviders()` before `builder.ApplyTo(web.Host)`
  so the CLI's Serilog provider is the only sink.
- **Shared configuration** flows through. `appsettings.json` is loaded by
  `UseDefaultConfiguration`; the `WEBHOST_` environment-variable prefix overrides
  it; and `ApplyTo` carries both sources onto the `WebApplicationBuilder`.
- **Standard verbosity flags work everywhere** — `-v` / `-q` / `--verbosity` apply
  to the web host too, because `ApplyTo` registers the `ParseResult` singleton
  that `UseSerilog`'s factory reads.

## Usage

```console
$ dotnet run -- status
[14:21:07.790 INF] WebHost.StatusCommand: Hello, World! (from appsettings.json)

$ dotnet run -- status --name Alice -v
[14:21:08.929 INF] WebHost.StatusCommand: Hello, Alice! (from appsettings.json)
[14:21:08.941 DBG] WebHost.StatusCommand: Template source: Hello, {name}! (from appsettings.json)

$ dotnet run -- serve --port 5000
[14:25:19.911 INF] WebHost.ServeCommand: Starting HTTP server on port 5000
[14:25:19.978 INF] Microsoft.Hosting.Lifetime: Now listening on: http://localhost:5000
...
$ curl http://localhost:5000/greet/Alice
Hello, Alice! (from appsettings.json)

$ WEBHOST_GREETING__TEMPLATE="Howdy, {name}!" dotnet run -- serve --port 5000
$ curl http://localhost:5000/greet/Alice
Howdy, Alice!
```
