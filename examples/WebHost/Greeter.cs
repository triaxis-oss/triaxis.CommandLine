namespace WebHost;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using triaxis.CommandLine;

public interface IGreeter
{
    string Greet(string name);
}

/// <summary>
/// Shared service: reads its greeting template from configuration. Registered at the
/// builder level so both the CLI command and the web server see the same instance.
/// </summary>
public class ConfigurableGreeter(IConfiguration configuration) : IGreeter
{
    public string Greet(string name)
    {
        var template = configuration["Greeting:Template"] ?? "Hello, {name}!";
        return template.Replace("{name}", name);
    }
}

/// <summary>
/// Service-registration hook picked up by the source-generated entry point. Every
/// command in this assembly (the CLI <c>status</c> and the standalone <c>serve</c>)
/// sees the same <see cref="IGreeter"/> instance.
/// </summary>
internal static class Startup
{
    [ConfigureServices]
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<IGreeter, ConfigurableGreeter>();
    }
}

