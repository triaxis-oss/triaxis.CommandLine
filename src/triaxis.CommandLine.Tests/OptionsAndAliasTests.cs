namespace triaxis.CommandLine.Tests;

using Microsoft.Extensions.DependencyInjection;

public class DbConfig
{
    [Option("--connection-string")]
    public string? ConnectionString { get; set; }

    [Option("--timeout")]
    public int Timeout { get; set; }
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

[TestFixture]
public class OptionsAndAliasTests
{
    [SetUp]
    public void Reset() => DbPingCommand.LastConfig = null;

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
}
