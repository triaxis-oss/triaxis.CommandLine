namespace WebHost;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using triaxis.CommandLine;

[Command("serve", Description = "Runs the greeter as an HTTP server.")]
public class ServeCommand
{
    [Option("--port", Description = "Port to listen on")]
    public int Port { get; set; } = 5000;

    public async Task<int> MainAsync(IToolBuilder builder, CancellationToken ct)
    {
        // Build our own host for the web server; inherit every service / config source
        // / Serilog wiring from the CLI builder via a single ApplyTo call. Anything
        // ConfigureServices registered at the CLI level (e.g. IGreeter) is available
        // here too; configuration sources (appsettings.json + WEBHOST_ env vars) are
        // carried over so the shared Greeting:Template setting applies equally.
        var web = WebApplication.CreateBuilder();
        web.Logging.ClearProviders();   // drop ASP.NET Core's defaults so Serilog is the only provider
        builder.ApplyTo(web.Host);
        web.WebHost.UseUrls($"http://localhost:{Port}");

        await using var app = web.Build();

        var logger = app.Services.GetRequiredService<ILogger<ServeCommand>>();
        logger.LogInformation("Starting HTTP server on port {Port}", Port);

        app.MapGet("/", (IGreeter greeter) => greeter.Greet("World"));
        app.MapGet("/greet/{name}", (IGreeter greeter, string name) => greeter.Greet(name));

        await app.RunAsync(ct);
        logger.LogInformation("HTTP server stopped.");
        return 0;
    }
}
