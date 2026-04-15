namespace WebHost;

using Microsoft.Extensions.Configuration;

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
