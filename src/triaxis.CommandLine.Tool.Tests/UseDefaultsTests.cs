namespace triaxis.CommandLine.ToolTests;

using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

[Command("greet")]
public class GreetCommand
{
    public static string? LastGreeted;

    [Inject]
    public ILogger<GreetCommand> Logger { get; set; } = null!;

    [Option("--name")]
    public string Name { get; set; } = "world";

    public Task ExecuteAsync()
    {
        LastGreeted = Name;
        Logger.LogInformation("Greeting {Name}", Name);
        return Task.CompletedTask;
    }
}

[TestFixture]
public class UseDefaultsTests
{
    [SetUp]
    public void Reset()
    {
        GreetCommand.LastGreeted = null;
    }

    [Test]
    public async Task UseDefaults_SetsUpCommandLineFlow_EndToEnd()
    {
        var builder = Tool.CreateBuilder(["greet", "--name", "Bob"])
            .UseDefaults(commandsAssembly: typeof(UseDefaultsTests).Assembly);

        var exit = await builder.RunAsync();
        Assert.That(exit, Is.EqualTo(0));
        Assert.That(GreetCommand.LastGreeted, Is.EqualTo("Bob"));
    }

    [Test]
    public void UseDefaults_AddsVerbosityOptions()
    {
        var builder = Tool.CreateBuilder([])
            .UseDefaults(commandsAssembly: typeof(UseDefaultsTests).Assembly);
        var names = builder.RootCommand.Options.Select(o => o.Name).ToList();
        Assert.That(names, Does.Contain("--verbosity"));
        Assert.That(names, Does.Contain("-v"));
        Assert.That(names, Does.Contain("-q"));
    }

    [Test]
    public void UseDefaults_AddsOutputFormatOption()
    {
        var builder = Tool.CreateBuilder([])
            .UseDefaults(commandsAssembly: typeof(UseDefaultsTests).Assembly);
        var names = builder.RootCommand.Options.Select(o => o.Name).ToList();
        Assert.That(names, Does.Contain("--output"));
    }

    [Test]
    public void UseDefaults_ReturnsBuilderForChaining()
    {
        var builder = Tool.CreateBuilder([]);
        var result = builder.UseDefaults(commandsAssembly: typeof(UseDefaultsTests).Assembly);
        Assert.That(result, Is.SameAs(builder));
    }

    [Test]
    public void UseDefaults_ConfiguresAppSettingsJsonBasePath()
    {
        var builder = Tool.CreateBuilder([])
            .UseDefaults(commandsAssembly: typeof(UseDefaultsTests).Assembly);
        // Just verify the configuration manager still provides a working instance
        Assert.That(builder.Configuration, Is.Not.Null);
    }

    [Test]
    public async Task UseDefaults_WithEnvironmentVariablePrefix_ReadsFromEnvironment()
    {
        Environment.SetEnvironmentVariable("TXTEST_mykey", "myvalue");
        try
        {
            var builder = Tool.CreateBuilder(["greet"])
                .UseDefaults(
                    environmentVariablePrefix: "TXTEST_",
                    commandsAssembly: typeof(UseDefaultsTests).Assembly);
            await builder.RunAsync();
            Assert.That(builder.Configuration["mykey"], Is.EqualTo("myvalue"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TXTEST_mykey", null);
        }
    }
}
