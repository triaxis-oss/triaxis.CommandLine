namespace BindingShowcase;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// ═══════════════════════════════════════════════════════════════════
// Root-level command (no path — sets the RootCommand's action)
// ═══════════════════════════════════════════════════════════════════

[Command(Description = "Root-level command that runs when no subcommand is specified")]
public class DefaultCommand
{
    [Option("--info")]
    public bool Info { get; set; }

    public Task ExecuteAsync()
    {
        if (Info)
        {
            Console.WriteLine("BindingShowcase v1.0");
        }
        else
        {
            Console.WriteLine("Use --help to see available commands");
        }
        return Task.CompletedTask;
    }
}

// ═══════════════════════════════════════════════════════════════════
// Public property binding — the common case
// ═══════════════════════════════════════════════════════════════════

[Command("public-props", Description = "Public property binding")]
public class PublicPropsCommand
{
    [Argument("name", Description = "Name argument")]
    public string Name { get; set; } = "World";

    [Option("--greeting", Description = "Greeting to use")]
    public string Greeting { get; set; } = "Hello";

    [Option("--count", Description = "Repeat count")]
    public int Count { get; set; } = 1;

    [Option("--verbose")]
    public bool Verbose { get; set; }

    public Task ExecuteAsync()
    {
        for (var i = 0; i < Count; i++)
        {
            Console.WriteLine($"{Greeting} {Name}!");
        }
        if (Verbose)
        {
            Console.WriteLine($"(repeated {Count} times)");
        }
        return Task.CompletedTask;
    }
}

// ═══════════════════════════════════════════════════════════════════
// Private field binding
// ═══════════════════════════════════════════════════════════════════

[Command("private-fields", Description = "Private field binding")]
public class PrivateFieldsCommand
{
    [Argument("value")]
    private int _value = 0;

    [Option("--tag")]
    private string _tag = "default";

    [Inject]
    private readonly ILogger<PrivateFieldsCommand> _logger = null!;

    public Task ExecuteAsync()
    {
        _logger.LogInformation("Value={Value}, Tag={Tag}", _value, _tag);
        Console.WriteLine($"Value: {_value}, Tag: {_tag}");
        return Task.CompletedTask;
    }
}

// ═══════════════════════════════════════════════════════════════════
// Required members (C# required keyword)
// ═══════════════════════════════════════════════════════════════════

[Command("required-members", Description = "C# required keyword binding")]
public class RequiredMembersCommand
{
    [Option("--key")]
    public required string Key { get; set; }

    [Argument("message")]
    public required string Message { get; set; }

    [Inject]
    public required ILogger<RequiredMembersCommand> Logger { get; set; }

    public Task ExecuteAsync()
    {
        Logger.LogInformation("Key={Key}", Key);
        Console.WriteLine($"[{Key}] {Message}");
        return Task.CompletedTask;
    }
}

// ═══════════════════════════════════════════════════════════════════
// Init-only properties
// ═══════════════════════════════════════════════════════════════════

[Command("init-props", Description = "Init-only property binding")]
public class InitPropsCommand
{
    [Argument("path")]
    public string Path { get; init; } = ".";

    [Option("--recursive")]
    public bool Recursive { get; init; }

    public Task ExecuteAsync()
    {
        Console.WriteLine($"Path: {Path}, Recursive: {Recursive}");
        return Task.CompletedTask;
    }
}

// ═══════════════════════════════════════════════════════════════════
// [Options] nested object binding
// ═══════════════════════════════════════════════════════════════════

public class ConnectionOptions
{
    [Option("--host", Description = "Database host")]
    public string Host { get; set; } = "localhost";

    [Option("--port", Description = "Database port")]
    public int Port { get; set; } = 5432;

    [Option("--database", Description = "Database name")]
    public string Database { get; set; } = "mydb";
}

[Command("connect", Description = "[Options] nested object binding")]
public class ConnectCommand
{
    [Options]
    public ConnectionOptions Connection { get; set; } = new();

    [Option("--timeout")]
    public int Timeout { get; set; } = 30;

    public Task ExecuteAsync()
    {
        Console.WriteLine($"Connecting to {Connection.Host}:{Connection.Port}/{Connection.Database} (timeout={Timeout}s)");
        return Task.CompletedTask;
    }
}

// ═══════════════════════════════════════════════════════════════════
// Required [Options] property + required members on [Options] type
// ═══════════════════════════════════════════════════════════════════

public class AuthOptions
{
    [Option("--token")]
    public required string Token { get; set; }

    [Option("--scope")]
    public string Scope { get; set; } = "read";
}

public class RetryOptions
{
    [Option("--max-retries")]
    public int MaxRetries { get; set; } = 3;

    [Option("--delay")]
    public int DelayMs { get; set; } = 1000;
}

[Command("req-opts", Description = "Required [Options] property with required members")]
public class RequiredOptionsCommand
{
    [Options]
    public required AuthOptions Auth { get; init; }

    [Options]
    public RetryOptions Retry { get; set; } = new();

    public Task ExecuteAsync()
    {
        Console.WriteLine($"Token: {Auth.Token}, Scope: {Auth.Scope}, Retries: {Retry.MaxRetries}, Delay: {Retry.DelayMs}");
        return Task.CompletedTask;
    }
}

// ═══════════════════════════════════════════════════════════════════
// Nested [Options] — [Options] inside [Options]
// ═══════════════════════════════════════════════════════════════════

public class InnerOptions
{
    [Option("--inner-value")]
    public string InnerValue { get; set; } = "default";
}

public class OuterOptions
{
    [Option("--outer-value")]
    public string OuterValue { get; set; } = "default";

    [Options]
    public InnerOptions Inner { get; set; } = new();
}

[Command("nested-opts", Description = "Nested [Options] inside [Options]")]
public class NestedOptionsCommand
{
    [Options]
    public OuterOptions Outer { get; set; } = new();

    public Task ExecuteAsync()
    {
        Console.WriteLine($"Outer: {Outer.OuterValue}, Inner: {Outer.Inner.InnerValue}");
        return Task.CompletedTask;
    }
}

// ═══════════════════════════════════════════════════════════════════
// Collection arguments and options
// ═══════════════════════════════════════════════════════════════════

[Command("collections", Description = "Collection binding")]
public class CollectionsCommand
{
    [Argument("files", Description = "Files to process")]
    public string[] Files { get; set; } = [];

    [Option("--tag", Description = "Tags to apply")]
    public List<string> Tags { get; set; } = [];

    public Task ExecuteAsync()
    {
        Console.WriteLine($"Files: {string.Join(", ", Files)}");
        Console.WriteLine($"Tags: {string.Join(", ", Tags)}");
        return Task.CompletedTask;
    }
}

// ═══════════════════════════════════════════════════════════════════
// Constructor injection
// ═══════════════════════════════════════════════════════════════════

[Command("ctor-inject", Description = "Constructor injection")]
public class CtorInjectCommand(ILogger<CtorInjectCommand> logger, IServiceProvider services, IGreeter greeter)
{
    [Option("--name")]
    public string Name { get; set; } = "World";

    public Task ExecuteAsync()
    {
        logger.LogInformation("Services available: {HasServices}", services is not null);
        Console.WriteLine(greeter.Greet(Name));
        return Task.CompletedTask;
    }
}

// ═══════════════════════════════════════════════════════════════════
// [ConfigureServices] hook — registers services with the generated entry point
// ═══════════════════════════════════════════════════════════════════

public interface IGreeter
{
    string Greet(string name);
}

public class HelloGreeter : IGreeter
{
    public string Greet(string name) => $"Hello {name} (via ctor injection)!";
}

public static class ShowcaseStartup
{
    [ConfigureServices]
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<IGreeter, HelloGreeter>();
    }
}

// ═══════════════════════════════════════════════════════════════════
// Option aliases
// ═══════════════════════════════════════════════════════════════════

[Command("aliases", Description = "Option with aliases")]
public class AliasesCommand
{
    [Option("--output", Aliases = ["-o"], Description = "Output path")]
    public string Output { get; set; } = "out.txt";

    [Option("--force", Aliases = ["-f"])]
    public bool Force { get; set; }

    public Task ExecuteAsync()
    {
        Console.WriteLine($"Output: {Output}, Force: {Force}");
        return Task.CompletedTask;
    }
}

// ═══════════════════════════════════════════════════════════════════
// Explicit Required attribute
// ═══════════════════════════════════════════════════════════════════

[Command("explicit-required", Description = "Required=true/false on attributes")]
public class ExplicitRequiredCommand
{
    [Argument("target", Required = true)]
    public string Target { get; set; } = "";

    [Argument("source", Required = false)]
    public string? Source { get; set; }

    [Option("--mode", Required = true)]
    public string Mode { get; set; } = "";

    public Task ExecuteAsync()
    {
        Console.WriteLine($"Target: {Target}, Source: {Source ?? "(none)"}, Mode: {Mode}");
        return Task.CompletedTask;
    }
}

// ═══════════════════════════════════════════════════════════════════
// Nested command paths (subcommands)
// ═══════════════════════════════════════════════════════════════════

[Command("db", "migrate", Description = "Run database migrations")]
public class DbMigrateCommand
{
    [Option("--target-version")]
    public int? TargetVersion { get; set; }

    public Task ExecuteAsync()
    {
        Console.WriteLine($"Migrating to {TargetVersion?.ToString() ?? "latest"}");
        return Task.CompletedTask;
    }
}

[Command("db", "seed", Description = "Seed database")]
public class DbSeedCommand
{
    [Argument("file", Description = "Seed data file")]
    public string File { get; set; } = "seed.json";

    public Task ExecuteAsync()
    {
        Console.WriteLine($"Seeding from {File}");
        return Task.CompletedTask;
    }
}
