namespace triaxis.CommandLine.Serilog.Tests;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

    [Test]
    public void UseDefaultLogging_AppliesSerilogAndVerbosityOptions()
    {
        var builder = Tool.CreateBuilder([]);
        var result = builder.UseDefaultLogging();

        Assert.That(result, Is.SameAs(builder), "UseDefaultLogging should return the same builder for chaining");
        var names = builder.RootCommand.Options.Select(o => o.Name).ToList();
        Assert.That(names, Does.Contain("--verbosity"));
        Assert.That(names, Does.Contain("-v"));
        Assert.That(names, Does.Contain("-q"));
    }

    [Test]
    public void UseSerilog_OnPlainHostBuilder_RegistersLoggerFactory()
    {
        // The UseSerilog extension should be usable on any IHostBuilder, not only
        // IToolBuilder — so that an alternate host (e.g. WebApplication.Host) can
        // reuse the same logging bootstrap.
        using var host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .Build();

        var factory = host.Services.GetRequiredService<ILoggerFactory>();
        Assert.That(factory, Is.Not.Null);

        // ParseResult is not registered on a plain host, so verbosity falls back to
        // whatever ReadFrom.Configuration / defaults produce — logging must still
        // be resolvable without throwing.
        var logger = factory.CreateLogger("test");
        Assert.That(logger, Is.Not.Null);
    }

    [Test]
    public async Task UseSerilog_OnPlainHostBuilder_WithoutParseResult_UsesDefaultLevel()
    {
        // Verbosity wiring reads ParseResult from DI; on alternate hosts where that
        // service is not registered, UseSerilog must gracefully skip the verbosity
        // override instead of throwing.
        using var host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .Build();
        await host.StartAsync();
        try
        {
            var logger = host.Services.GetRequiredService<ILogger<UseSerilogTests>>();
            // Information is Serilog's default; verify it's enabled and constructing
            // the provider did not throw (which it would if GetRequiredService<ParseResult>
            // were still used).
            Assert.That(logger.IsEnabled(LogLevel.Information), Is.True);
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
