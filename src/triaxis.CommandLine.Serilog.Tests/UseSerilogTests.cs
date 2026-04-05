namespace triaxis.CommandLine.Serilog.Tests;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

[TestFixture]
public class UseSerilogTests
{
    [Command("noop")]
    public class NoopCommand
    {
        [Inject]
        public ILogger<NoopCommand> Logger { get; set; } = null!;

        public static bool Ran;
        public static LogLevel? ResolvedMinLevel;

        public Task ExecuteAsync()
        {
            Ran = true;
            if (Logger.IsEnabled(LogLevel.Trace))
            {
                ResolvedMinLevel = LogLevel.Trace;
            }
            else if (Logger.IsEnabled(LogLevel.Debug))
            {
                ResolvedMinLevel = LogLevel.Debug;
            }
            else if (Logger.IsEnabled(LogLevel.Information))
            {
                ResolvedMinLevel = LogLevel.Information;
            }
            else if (Logger.IsEnabled(LogLevel.Warning))
            {
                ResolvedMinLevel = LogLevel.Warning;
            }
            else
            {
                ResolvedMinLevel = LogLevel.Error;
            }
            return Task.CompletedTask;
        }
    }

    [SetUp]
    public void Reset()
    {
        NoopCommand.Ran = false;
        NoopCommand.ResolvedMinLevel = null;
    }

    [Test]
    public async Task UseSerilog_RegistersLoggerFactory_AndResolvesLogger()
    {
        var builder = Tool.CreateBuilder(["noop"])
            .UseSerilog()
            .UseVerbosityOptions();
        builder.AddCommandsFromAssembly(typeof(UseSerilogTests).Assembly);

        var exit = await builder.RunAsync();
        Assert.That(exit, Is.EqualTo(0));
        Assert.That(NoopCommand.Ran, Is.True);
        Assert.That(NoopCommand.ResolvedMinLevel, Is.EqualTo(LogLevel.Information));
    }

    [Test]
    public async Task UseSerilog_RespectsVerboseFlag()
    {
        var builder = Tool.CreateBuilder(["noop", "-v"])
            .UseSerilog()
            .UseVerbosityOptions();
        builder.AddCommandsFromAssembly(typeof(UseSerilogTests).Assembly);

        await builder.RunAsync();
        Assert.That(NoopCommand.ResolvedMinLevel, Is.EqualTo(LogLevel.Debug));
    }

    [Test]
    public async Task UseSerilog_RespectsQuietFlag()
    {
        var builder = Tool.CreateBuilder(["noop", "-q"])
            .UseSerilog()
            .UseVerbosityOptions();
        builder.AddCommandsFromAssembly(typeof(UseSerilogTests).Assembly);

        await builder.RunAsync();
        Assert.That(NoopCommand.ResolvedMinLevel, Is.EqualTo(LogLevel.Warning));
    }

    [Test]
    public void UseVerbosityOptions_AddsOptionsToRoot()
    {
        var builder = Tool.CreateBuilder([]).UseVerbosityOptions();
        var names = builder.RootCommand.Options.Select(o => o.Name).ToList();
        Assert.That(names, Does.Contain("--verbosity"));
        Assert.That(names, Does.Contain("-v"));
        Assert.That(names, Does.Contain("-q"));
    }

    [Test]
    public void UseSerilog_ReturnsSameBuilderForChaining()
    {
        var builder = Tool.CreateBuilder([]);
        var result = builder.UseSerilog();
        Assert.That(result, Is.SameAs(builder));
    }
}
