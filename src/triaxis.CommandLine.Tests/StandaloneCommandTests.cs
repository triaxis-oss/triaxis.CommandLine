namespace triaxis.CommandLine.Tests;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

[TestFixture]
public class StandaloneCommandTests
{
    [SetUp]
    public void Reset()
    {
        SimpleMain.Ran = false;
        SimpleMain.Exit = null;
        CtMain.LastToken = default;
        CtMain.Ran = false;
        BuilderMain.ReceivedBuilder = null;
        BuilderMain.ConfigValue = null;
        BuilderMain.ResolvedPort = 0;
        BuilderMain.TargetPropertiesHadInvocationContext = false;
        BuilderMain.DelegateObservedInvocationContext = null;
        DestructiveMain.TargetKey = null;
        DestructiveMain.ToolKey = null;
        BaseMainAsync.Ran = false;
    }

    [Test]
    public async Task MainAsync_RunsWithoutBuildingServiceProvider()
    {
        var builder = Tool.CreateBuilder(["simple-main"]);
        builder.ConfigureServices(s => s.AddSingleton(new ObservableToken()));
        builder.AddCommandsFromAssembly(typeof(StandaloneCommandTests).Assembly);

        var exit = await builder.RunAsync();

        Assert.That(exit, Is.EqualTo(0));
        Assert.That(SimpleMain.Ran, Is.True);

        // The CLI-side service provider was never constructed for a standalone command;
        // the service provider accessor still returns null.
        Assert.That(builder.GetServiceProviderAccessor()(), Is.Null,
            "Standalone commands must not force the CLI service provider to be built.");
    }

    [Test]
    public async Task MainAsync_WithTaskOfInt_ReturnsExitCode()
    {
        var builder = Tool.CreateBuilder(["simple-main", "--code", "42"]);
        builder.AddCommandsFromAssembly(typeof(StandaloneCommandTests).Assembly);

        SimpleMain.Exit = 42;
        var exit = await builder.RunAsync();

        Assert.That(exit, Is.EqualTo(42));
    }

    [Test]
    public async Task MainAsync_ReceivesCancellationToken()
    {
        var builder = Tool.CreateBuilder(["ct-main"]);
        builder.AddCommandsFromAssembly(typeof(StandaloneCommandTests).Assembly);

        await builder.RunAsync();

        // The current StandaloneHost passes CancellationToken.None — the command
        // is expected to wire its own process-termination handling if needed. The
        // observable contract for now is that MainAsync's ct parameter is invoked.
        Assert.That(CtMain.Ran, Is.True);
    }

    [Test]
    public async Task MainAsync_WithToolBuilder_CanReceiveItAndApplyToAlternateHost()
    {
        var builder = Tool.CreateBuilder(["builder-main", "--port", "8080"]);
        ((IConfigurationBuilder)builder.Configuration).AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["apply_key"] = "apply_value",
        });
        builder.ConfigureServices(s => s.AddSingleton("shared-token"));
        builder.AddCommandsFromAssembly(typeof(StandaloneCommandTests).Assembly);

        var exit = await builder.RunAsync();

        Assert.That(exit, Is.EqualTo(0));
        Assert.That(BuilderMain.ReceivedBuilder, Is.SameAs(builder));
        Assert.That(BuilderMain.ResolvedPort, Is.EqualTo(8080));
        Assert.That(BuilderMain.ConfigValue, Is.EqualTo("apply_value"),
            "ApplyTo should carry the CLI-side configuration sources onto the alternate host.");
        Assert.That(BuilderMain.TargetPropertiesHadInvocationContext, Is.True,
            "ApplyTo should seed InvocationContextKey on the target's Properties.");
        Assert.That(BuilderMain.DelegateObservedInvocationContext?.ParseResult, Is.SameAs(builder.Parse()),
            "A ConfigureServices delegate on the target should be able to call ctx.GetInvocationContext().");
    }

    [Test]
    public async Task MainAsync_ApplyTo_IsolatesToolConfigurationFromTarget()
    {
        var builder = Tool.CreateBuilder(["destructive-main"]);
        // Simulate a deferred tool-side delegate (UseMomentumOperationsConfiguration-style)
        // that destructively clears sources and installs its own. Against a non-isolated
        // replay this would wipe the target's own sources too.
        ((IHostBuilder)builder).ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.Sources.Clear();
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["tool_key"] = "tool_value",
            });
        });
        builder.AddCommandsFromAssembly(typeof(StandaloneCommandTests).Assembly);

        await builder.RunAsync();

        // The destructive delegate ran against the tool's scratch builder only — the
        // target's own user-added source survived.
        Assert.That(DestructiveMain.TargetKey, Is.EqualTo("target_value"),
            "A destructive tool-side delegate must not wipe user-added sources on the target.");
        // And the tool layer's contribution is still visible on the target.
        Assert.That(DestructiveMain.ToolKey, Is.EqualTo("tool_value"),
            "The tool's scratch configuration should be merged onto the target as a single source.");
    }

    [Test]
    public async Task MainAsync_InheritedFromBaseClass_IsDetected()
    {
        var builder = Tool.CreateBuilder(["inherit-main"]);
        builder.AddCommandsFromAssembly(typeof(StandaloneCommandTests).Assembly);

        using (var host = ((IHostBuilder)builder).Build())
        {
            Assert.That(host, Is.InstanceOf<StandaloneHost>(),
                "A command that only inherits MainAsync from a base class must still resolve as standalone.");
        }

        var exit = await builder.RunAsync();

        Assert.That(exit, Is.EqualTo(0));
        Assert.That(BaseMainAsync.Ran, Is.True);
    }

    [Test]
    public void Build_ReturnsStandaloneHost_ForStandaloneCommand()
    {
        var builder = Tool.CreateBuilder(["simple-main"]);
        builder.AddCommandsFromAssembly(typeof(StandaloneCommandTests).Assembly);

        using var host = ((IHostBuilder)builder).Build();

        Assert.That(host, Is.InstanceOf<StandaloneHost>());
    }

    [Test]
    public void Build_ReturnsToolHost_ForRegularCommand()
    {
        var builder = Tool.CreateBuilder(["regular"]);
        builder.AddCommandsFromAssembly(typeof(StandaloneCommandTests).Assembly);

        using var host = ((IHostBuilder)builder).Build();

        Assert.That(host, Is.InstanceOf<ToolHost>());
    }

    [Test]
    public void Build_DoesNotShortCircuit_WhenHelpRequestedOnStandaloneCommand()
    {
        // --help resolves ParseResult.Action to System.CommandLine's HelpAction; we
        // must not short-circuit to StandaloneHost in that case, otherwise built-in
        // help (and --version) wouldn't render for standalone commands.
        var builder = Tool.CreateBuilder(["simple-main", "--help"]);
        builder.AddCommandsFromAssembly(typeof(StandaloneCommandTests).Assembly);

        using var host = ((IHostBuilder)builder).Build();

        Assert.That(host, Is.InstanceOf<ToolHost>(),
            "When --help is requested, ParseResult.Action is HelpAction — standalone short-circuit should not apply.");
    }
}

public class ObservableToken { }

[Command("simple-main")]
public class SimpleMain
{
    [Option("--code")]
    public int Code { get; set; }

    public static bool Ran { get; set; }
    public static int? Exit { get; set; }

    public Task<int> MainAsync()
    {
        Ran = true;
        return Task.FromResult(Exit ?? Code);
    }
}

[Command("ct-main")]
public class CtMain
{
    public static CancellationToken LastToken { get; set; }
    public static bool Ran { get; set; }

    public Task MainAsync(CancellationToken ct)
    {
        LastToken = ct;
        Ran = true;
        return Task.CompletedTask;
    }
}

[Command("builder-main")]
public class BuilderMain
{
    [Option("--port")]
    public int Port { get; set; }

    public static IToolBuilder? ReceivedBuilder { get; set; }
    public static string? ConfigValue { get; set; }
    public static int ResolvedPort { get; set; }
    public static bool TargetPropertiesHadInvocationContext { get; set; }
    public static InvocationContext? DelegateObservedInvocationContext { get; set; }

    public Task<int> MainAsync(IToolBuilder builder, CancellationToken ct)
    {
        ReceivedBuilder = builder;

        // Verify ApplyTo propagates direct config sources and services to an alternate host.
        var target = new TestHostBuilder();
        // A consumer-registered delegate on the target side should be able to observe
        // the InvocationContext that ApplyTo seeded.
        ((IHostBuilder)target).ConfigureServices((ctx, _) =>
        {
            DelegateObservedInvocationContext = ctx.GetInvocationContext();
        });
        builder.ApplyTo(target);
        TargetPropertiesHadInvocationContext =
            target.Properties.ContainsKey("triaxis.CommandLine.InvocationContext");
        var services = target.BuildServices();
        var config = services.GetRequiredService<IConfiguration>();
        ConfigValue = config["apply_key"];

        // Also verify the shared token registration came through.
        var token = services.GetRequiredService<string>();
        Assert.That(token, Is.EqualTo("shared-token"));

        // And ParseResult is exposed on the alternate host for downstream handlers.
        var pr = services.GetRequiredService<System.CommandLine.ParseResult>();
        Assert.That(pr, Is.SameAs(builder.Parse()));

        ResolvedPort = Port;
        return Task.FromResult(0);
    }
}

[Command("destructive-main")]
public class DestructiveMain
{
    public static string? TargetKey { get; set; }
    public static string? ToolKey { get; set; }

    public Task MainAsync(IToolBuilder builder)
    {
        var target = new TestHostBuilder();
        // The target owns its own config source — simulating a user-added --config
        // YAML on a WebApplicationBuilder. A destructive tool-side delegate must
        // NOT be able to reach in and clear it.
        ((IHostBuilder)target).ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["target_key"] = "target_value",
            });
        });
        builder.ApplyTo(target);
        var services = target.BuildServices();
        var config = services.GetRequiredService<IConfiguration>();
        TargetKey = config["target_key"];
        ToolKey = config["tool_key"];
        return Task.CompletedTask;
    }
}

internal sealed class TestHostBuilder : IHostBuilder
{
    private readonly List<Action<HostBuilderContext, IConfigurationBuilder>> _appCfg = [];
    private readonly List<Action<HostBuilderContext, IServiceCollection>> _configureServices = [];

    public IDictionary<object, object> Properties { get; } = new Dictionary<object, object>();

    public IHostBuilder ConfigureHostConfiguration(Action<IConfigurationBuilder> _) => this;

    public IHostBuilder ConfigureAppConfiguration(Action<HostBuilderContext, IConfigurationBuilder> d)
    { _appCfg.Add(d); return this; }

    public IHostBuilder ConfigureServices(Action<HostBuilderContext, IServiceCollection> d)
    { _configureServices.Add(d); return this; }

    public IHostBuilder UseServiceProviderFactory<TContainerBuilder>(IServiceProviderFactory<TContainerBuilder> _) where TContainerBuilder : notnull => throw new NotSupportedException();
    public IHostBuilder UseServiceProviderFactory<TContainerBuilder>(Func<HostBuilderContext, IServiceProviderFactory<TContainerBuilder>> _) where TContainerBuilder : notnull => throw new NotSupportedException();
    public IHostBuilder ConfigureContainer<TContainerBuilder>(Action<HostBuilderContext, TContainerBuilder> _) => throw new NotSupportedException();
    IHost IHostBuilder.Build() => throw new NotSupportedException();

    public IServiceProvider BuildServices()
    {
        var cfgBuilder = new ConfigurationBuilder();
        var ctx = new HostBuilderContext(Properties) { Configuration = new ConfigurationManager() };
        foreach (var a in _appCfg)
        {
            a(ctx, cfgBuilder);
        }
        IConfiguration cfg = cfgBuilder.Build();
        ctx.Configuration = cfg;

        var services = new ServiceCollection();
        services.AddSingleton(cfg);
        foreach (var a in _configureServices)
        {
            a(ctx, services);
        }
        return services.BuildServiceProvider();
    }
}

[Command("regular")]
public class RegularCommand
{
    public Task ExecuteAsync() => Task.CompletedTask;
}

public abstract class BaseMainAsync
{
    public static bool Ran { get; set; }

    public Task MainAsync()
    {
        Ran = true;
        return Task.CompletedTask;
    }
}

[Command("inherit-main")]
public class InheritMainCommand : BaseMainAsync
{
}
