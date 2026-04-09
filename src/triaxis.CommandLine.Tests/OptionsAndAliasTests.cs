namespace triaxis.CommandLine.Tests;

using Microsoft.Extensions.DependencyInjection;

public class DbConfig
{
    [Option("--connection-string")]
    public string? ConnectionString { get; set; }

    [Option("--timeout")]
    public int Timeout { get; set; }
}

public class BaseEndpointConfig
{
    [Option("--host")]
    public string Host { get; set; } = "localhost";

    [Option("--port")]
    public int Port { get; set; } = 8080;
}

public class ExtendedEndpointConfig : BaseEndpointConfig
{
    [Option("--scheme")]
    public string Scheme { get; set; } = "https";
}

[Command("dbping", Aliases = ["ping", "ping-db"], Description = "Pings the DB")]
public class DbPingCommand
{
    public static DbConfig? LastConfig;

    [Options]
    public DbConfig Options { get; set; } = new();

    public Task ExecuteAsync()
    {
        LastConfig = new DbConfig
        {
            ConnectionString = Options.ConnectionString,
            Timeout = Options.Timeout,
        };
        return Task.CompletedTask;
    }
}

[Command("endpoint")]
public class EndpointCommand
{
    public static ExtendedEndpointConfig? LastConfig;

    [Options]
    public ExtendedEndpointConfig Config { get; set; } = new();

    public Task ExecuteAsync()
    {
        LastConfig = new ExtendedEndpointConfig
        {
            Host = Config.Host,
            Port = Config.Port,
            Scheme = Config.Scheme,
        };
        return Task.CompletedTask;
    }
}

[TestFixture]
public class OptionsAndAliasTests
{
    [SetUp]
    public void Reset()
    {
        DbPingCommand.LastConfig = null;
        EndpointCommand.LastConfig = null;
    }

    private static IToolBuilder CreateBuilder(string[] args)
    {
        var builder = Tool.CreateBuilder(args);
        builder.AddCommandsFromAssembly(typeof(OptionsAndAliasTests).Assembly);
        return builder;
    }

    [Test]
    public async Task NestedOptionsAttribute_BindsMembersOfInnerClass()
    {
        var builder = CreateBuilder(["dbping", "--connection-string", "Host=x", "--timeout", "45"]);
        var exit = await builder.RunAsync();

        Assert.That(exit, Is.EqualTo(0));
        Assert.That(DbPingCommand.LastConfig, Is.Not.Null);
        Assert.That(DbPingCommand.LastConfig!.ConnectionString, Is.EqualTo("Host=x"));
        Assert.That(DbPingCommand.LastConfig.Timeout, Is.EqualTo(45));
    }

    [Test]
    public async Task CommandAlias_InvokesSameCommandAsPrimaryName()
    {
        var builder = CreateBuilder(["ping", "--connection-string", "aliased"]);
        var exit = await builder.RunAsync();
        Assert.That(exit, Is.EqualTo(0));
        Assert.That(DbPingCommand.LastConfig?.ConnectionString, Is.EqualTo("aliased"));
    }

    [Test]
    public async Task SecondaryAlias_AlsoInvokesCommand()
    {
        var builder = CreateBuilder(["ping-db", "--connection-string", "ping-db-alias"]);
        var exit = await builder.RunAsync();
        Assert.That(exit, Is.EqualTo(0));
        Assert.That(DbPingCommand.LastConfig?.ConnectionString, Is.EqualTo("ping-db-alias"));
    }

    [Test]
    public void CommandDescription_IsAppliedFromAttribute()
    {
        var builder = CreateBuilder([]);
        var cmd = builder.GetCommand("dbping");
        Assert.That(cmd.Description, Is.EqualTo("Pings the DB"));
    }

    [Test]
    public async Task NestedOptionsAttribute_BindsBaseClassMembers()
    {
        var builder = CreateBuilder(["endpoint", "--host", "example.com", "--port", "9090", "--scheme", "http"]);
        var exit = await builder.RunAsync();

        Assert.That(exit, Is.EqualTo(0));
        Assert.That(EndpointCommand.LastConfig, Is.Not.Null);
        Assert.That(EndpointCommand.LastConfig!.Host, Is.EqualTo("example.com"));
        Assert.That(EndpointCommand.LastConfig.Port, Is.EqualTo(9090));
        Assert.That(EndpointCommand.LastConfig.Scheme, Is.EqualTo("http"));
    }

    [Test]
    public async Task NestedOptionsAttribute_BaseClassMembersUseDefaults()
    {
        var builder = CreateBuilder(["endpoint", "--scheme", "http"]);
        var exit = await builder.RunAsync();

        Assert.That(exit, Is.EqualTo(0));
        Assert.That(EndpointCommand.LastConfig, Is.Not.Null);
        Assert.That(EndpointCommand.LastConfig!.Host, Is.EqualTo("localhost"));
        Assert.That(EndpointCommand.LastConfig.Port, Is.EqualTo(8080));
        Assert.That(EndpointCommand.LastConfig.Scheme, Is.EqualTo("http"));
    }

    [Test]
    public void Options_AppearInDeclarationOrder_NotAlphabetical()
    {
        var builder = CreateBuilder([]);
        var cmd = builder.GetCommand("endpoint");
        var optionNames = cmd.Options.Select(o => o.Name).ToList();

        // Derived class members first (declaration order), then base class members
        Assert.That(optionNames, Is.EqualTo(new[] { "--scheme", "--host", "--port" }));
    }

    [Test]
    public void NestedOptions_PreserveDeclarationOrder()
    {
        var builder = CreateBuilder([]);
        var cmd = builder.GetCommand("dbping");
        var optionNames = cmd.Options.Select(o => o.Name).ToList();

        // Declaration order: --connection-string, --timeout
        Assert.That(optionNames, Is.EqualTo(new[] { "--connection-string", "--timeout" }));
    }
}
