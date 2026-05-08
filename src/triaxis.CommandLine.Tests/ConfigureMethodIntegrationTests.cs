namespace triaxis.CommandLine.Tests;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

/// <summary>
/// End-to-end coverage for per-command static <c>Configure</c> methods. The hook fires
/// only for the command that the parsed command line actually selects; sibling commands'
/// hooks stay dormant.
/// </summary>
[TestFixture]
public class ConfigureMethodIntegrationTests
{
    [SetUp]
    public void Reset()
    {
        ConfigureProbeCommand.ConfigureCount = 0;
        ConfigureProbeCommand.LastBuilder = null;
        ConfigureProbeCommand.LastServices = null;
        ConfigureProbeCommand.ResolvedMarker = null;
        ConfigureSiblingCommand.ConfigureCount = 0;
        ConfigureSiblingCommand.Ran = false;
    }

    [Test]
    public async Task Configure_RunsOnce_WhenItsOwnCommandIsInvoked()
    {
        var builder = Tool.CreateBuilder(["configure-probe"]);
        builder.AddCommandsFromAssembly(typeof(ConfigureMethodIntegrationTests).Assembly);

        var exit = await builder.RunAsync();
        Assert.That(exit, Is.EqualTo(0));

        Assert.That(ConfigureProbeCommand.ConfigureCount, Is.EqualTo(1),
            "Configure must run exactly once when its command is invoked");
        Assert.That(ConfigureProbeCommand.LastBuilder, Is.SameAs(builder));
        Assert.That(ConfigureProbeCommand.LastServices, Is.Not.Null);
        Assert.That(ConfigureProbeCommand.ResolvedMarker, Is.EqualTo("configure-marker"),
            "the singleton registered by Configure must be resolvable from the host");
    }

    [Test]
    public async Task Configure_DoesNotRun_WhenADifferentCommandIsInvoked()
    {
        var builder = Tool.CreateBuilder(["configure-sibling"]);
        builder.AddCommandsFromAssembly(typeof(ConfigureMethodIntegrationTests).Assembly);

        var exit = await builder.RunAsync();
        Assert.That(exit, Is.EqualTo(0));
        Assert.That(ConfigureSiblingCommand.Ran, Is.True);

        Assert.That(ConfigureProbeCommand.ConfigureCount, Is.EqualTo(0),
            "Configure on a sibling command must stay dormant when its own command isn't invoked");
        Assert.That(ConfigureProbeCommand.ResolvedMarker, Is.Null);
    }

    [Test]
    public void Configure_DoesNotRun_ForBuiltInActions()
    {
        // --help routes through System.CommandLine's HelpAction, which is not the
        // command's own action — so the per-command Configure hook must not fire.
        // We Build (not Run) to avoid actually rendering help to the console; that's
        // enough since Configure is invoked from inside Build().
        var builder = Tool.CreateBuilder(["configure-probe", "--help"]);
        builder.AddCommandsFromAssembly(typeof(ConfigureMethodIntegrationTests).Assembly);

        using var host = ((IHostBuilder)builder).Build();

        Assert.That(ConfigureProbeCommand.ConfigureCount, Is.EqualTo(0),
            "Built-in actions like --help must not trigger Configure");
    }

    [Test]
    public void Configure_DoesNotRun_OnAddCommandsFromAssemblyAlone()
    {
        // Adding the assembly's commands must not be enough to fire Configure —
        // the hook is bound to action invocation, not registration.
        var builder = Tool.CreateBuilder(["configure-sibling"]);
        builder.AddCommandsFromAssembly(typeof(ConfigureMethodIntegrationTests).Assembly);

        Assert.That(ConfigureProbeCommand.ConfigureCount, Is.EqualTo(0));
        Assert.That(ConfigureSiblingCommand.ConfigureCount, Is.EqualTo(0));
    }
}

[Command("configure-probe")]
public class ConfigureProbeCommand
{
    public static int ConfigureCount;
    public static IToolBuilder? LastBuilder;
    public static IServiceCollection? LastServices;
    public static string? ResolvedMarker;

    [Inject]
    public IServiceProvider Provider { get; set; } = null!;

    public static void Configure(IToolBuilder builder, IServiceCollection services)
    {
        ConfigureCount++;
        LastBuilder = builder;
        LastServices = services;
        services.AddSingleton(new ConfigureMarker("configure-marker"));
    }

    public void Execute()
    {
        ResolvedMarker = Provider.GetService<ConfigureMarker>()?.Value;
    }
}

[Command("configure-sibling")]
public class ConfigureSiblingCommand
{
    public static int ConfigureCount;
    public static bool Ran;

    public static void Configure(IToolBuilder builder)
    {
        ConfigureCount++;
    }

    public void Execute()
    {
        Ran = true;
    }
}

public sealed record ConfigureMarker(string Value);
