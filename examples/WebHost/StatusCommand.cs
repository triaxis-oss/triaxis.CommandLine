namespace WebHost;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using triaxis.CommandLine;

[Command("status", Description = "Prints a greeting through the shared service.")]
public class StatusCommand
{
    [Inject]
    private readonly ILogger<StatusCommand> _logger = null!;

    [Inject]
    private readonly IGreeter _greeter = null!;

    [Inject]
    private readonly IConfiguration _configuration = null!;

    [Option("--name", "-n", Description = "Name to greet")]
    public string Name { get; set; } = "World";

    public Task ExecuteAsync()
    {
        _logger.LogInformation("{Greeting}", _greeter.Greet(Name));
        _logger.LogDebug("Template source: {Template}", _configuration["Greeting:Template"] ?? "(default)");
        return Task.CompletedTask;
    }
}
