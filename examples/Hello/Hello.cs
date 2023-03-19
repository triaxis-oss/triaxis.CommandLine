namespace Hello;

[Command("hello", Description = "Greets the world, or someone")]
public class Hello
{
    [Inject]
    private readonly ILogger<Hello> _logger = null!;

    [Argument("-n", "--name", Description = "Name of the person to greet", Required = false)]
    private readonly string? _name = "World";

    public Task ExecuteAsync()
    {
        _logger.LogDebug("Greeting {Name}...", _name);
        Console.WriteLine($"Hello {_name}!");
        return Task.CompletedTask;
    }
}
