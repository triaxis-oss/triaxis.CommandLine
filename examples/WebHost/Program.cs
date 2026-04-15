// Hand-written entry point so we can register a shared service at the builder level.
// Every command in this assembly sees the same configuration and logging setup;
// the `serve` standalone command replays that state onto its own WebApplication
// via `builder.ApplyTo(web.Host)`.

using triaxis.CommandLine;
using WebHost;

return await Tool.CreateBuilder(args)
    .AddCommandsFromAssembly()
    .UseSerilog()
    .UseVerbosityOptions()
    .UseDefaultConfiguration(environmentVariablePrefix: "WEBHOST_")
    .ConfigureServices(s => s.AddSingleton<IGreeter, ConfigurableGreeter>())
    .RunAsync();
