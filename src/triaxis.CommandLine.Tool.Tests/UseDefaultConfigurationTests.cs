namespace triaxis.CommandLine.ToolTests;

using System.CommandLine;

[TestFixture]
public class UseDefaultConfigurationTests
{
    [Test]
    public void UseDefaultConfiguration_WithNoArgs_StillExposesConfiguration()
    {
        var builder = Tool.CreateBuilder([]);
        var result = builder.UseDefaultConfiguration();
        Assert.That(result, Is.SameAs(builder));
        Assert.That(builder.Configuration, Is.Not.Null);
    }

    [Test]
    public async Task UseDefaultConfiguration_WithEnvironmentVariablePrefix_ReadsFromEnvironment()
    {
        Environment.SetEnvironmentVariable("TXCFG_mykey", "myvalue");
        try
        {
            var builder = Tool.CreateBuilder(["greet"])
                .UseSerilog()
                .UseVerbosityOptions()
                .UseDefaultConfiguration(environmentVariablePrefix: "TXCFG_")
                .AddCommandsFromAssembly(typeof(UseDefaultConfigurationTests).Assembly);

            await builder.RunAsync();
            Assert.That(builder.Configuration["mykey"], Is.EqualTo("myvalue"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TXCFG_mykey", null);
        }
    }

    [Test]
    public async Task GeneratedMainShape_NoObjectOutput_HasNoOutputOption()
    {
        // This mirrors what the source generator emits when no command produces output:
        // the chain omits .UseObjectOutput(), so --output should not be wired up.
        var builder = Tool.CreateBuilder([])
            .UseSerilog()
            .UseVerbosityOptions()
            .UseDefaultConfiguration()
            .AddCommandsFromAssembly(typeof(UseDefaultConfigurationTests).Assembly);

        var names = builder.RootCommand.Options.Select(o => o.Name).ToList();
        Assert.That(names, Does.Not.Contain("--output"));
        Assert.That(names, Does.Contain("--verbosity"));
        await Task.CompletedTask;
    }

    [Test]
    public void GeneratedMainShape_WithObjectOutput_HasOutputOption()
    {
        // And when .UseObjectOutput() is present, --output is wired up alongside the
        // rest of the defaults.
        var builder = Tool.CreateBuilder([])
            .UseSerilog()
            .UseVerbosityOptions()
            .UseObjectOutput()
            .UseDefaultConfiguration()
            .AddCommandsFromAssembly(typeof(UseDefaultConfigurationTests).Assembly);

        var names = builder.RootCommand.Options.Select(o => o.Name).ToList();
        Assert.That(names, Does.Contain("--output"));
        Assert.That(names, Does.Contain("--verbosity"));
    }
}
